using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.StateMachines;
using Xunit;

namespace Orchestrator.UnitTests;

public class RetryPolicyTests
{
    private static RetryPolicy Policy(Action<RetryOptions>? configure = null)
    {
        var o = new RetryOptions();
        configure?.Invoke(o);
        return new RetryPolicy(Options.Create(o));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(4, 600)]
    [InlineData(5, 1800)]
    [InlineData(6, 7200)]
    [InlineData(7, 21600)]
    public void Default_schedule_follows_the_PRD(int failedAttempt, double expectedSeconds) =>
        Policy().NextDelay(OperationType.PROVIDER_CREATE_PROCESS, failedAttempt, 0.5).TotalSeconds.Should().BeApproximately(expectedSeconds, 1e-6);

    [Fact]
    public void Default_is_eight_attempts_and_first_attempt_is_immediate()
    {
        var p = Policy();
        p.MaxAttemptsFor(OperationType.DOCUMENT_DOWNLOAD).Should().Be(8);
        p.NextDelay(OperationType.DOCUMENT_DOWNLOAD, 0, 0.5).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.2)]
    public void Jitter_stays_within_20_percent(double random, double factor) =>
        Policy().NextDelay(OperationType.PROVIDER_SEND_DOCUMENT, 3, random).TotalSeconds.Should().BeApproximately(120 * factor, 1e-6);

    [Fact]
    public void Random_jitter_never_leaves_the_band_nor_goes_negative()
    {
        var p = Policy();
        for (var attempt = 0; attempt <= 12; attempt++)
            for (var i = 0; i < 200; i++)
            {
                var baseSeconds = RetryPolicy.StandardSchedule[Math.Min(attempt, RetryPolicy.StandardSchedule.Length - 1)];
                var d = p.NextDelay(OperationType.PROVIDER_CREATE_PROCESS, attempt).TotalSeconds;
                d.Should().BeGreaterThanOrEqualTo(0);
                d.Should().BeInRange(baseSeconds * 0.8 - 1e-6, baseSeconds * 1.2 + 1e-6);
            }
    }

    [Fact]
    public void Policy_is_configurable_per_operation_type()
    {
        var p = Policy(o => o.Operations["SIGNED_DOCUMENT_DOWNLOAD"] = new RetryTypeOptions { MaxAttempts = 2, DelaysSeconds = [0, 1] });
        p.MaxAttemptsFor(OperationType.SIGNED_DOCUMENT_DOWNLOAD).Should().Be(2);
        p.NextDelay(OperationType.SIGNED_DOCUMENT_DOWNLOAD, 1, 0.5).TotalSeconds.Should().BeApproximately(1, 1e-6);
        p.MaxAttemptsFor(OperationType.DOCUMENT_DOWNLOAD).Should().Be(8);
    }

    [Fact]
    public void Empty_schedule_falls_back_to_capped_exponential_backoff()
    {
        var p = Policy(o => o.Default = new RetryTypeOptions { DelaysSeconds = [], BaseDelaySeconds = 5, Multiplier = 2, MaxDelaySeconds = 100 });
        double D(int n) => p.NextDelay(OperationType.PROVIDER_STATUS_CHECK, n, 0.5).TotalSeconds;
        D(1).Should().BeApproximately(5, 1e-6);
        D(2).Should().BeApproximately(10, 1e-6);
        D(3).Should().BeApproximately(20, 1e-6);
        D(10).Should().BeApproximately(100, 1e-6);
    }

    [Fact]
    public void Max_attempts_of_one_is_respected() =>
        Policy(o => o.Default = new RetryTypeOptions { MaxAttempts = 1 }).MaxAttemptsFor(OperationType.DOCUMENT_STORE).Should().Be(1);

    [Fact]
    public void Binds_from_configuration()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retry:Jitter"] = "0.1",
            ["Retry:Default:MaxAttempts"] = "3",
            ["Retry:Default:DelaysSeconds:0"] = "0",
            ["Retry:Default:DelaysSeconds:1"] = "7",
            ["Retry:Operations:DOCUMENT_DOWNLOAD:MaxAttempts"] = "1",
        }).Build();
        var opts = new RetryOptions();
        cfg.GetSection("Retry").Bind(opts);
        var p = new RetryPolicy(Options.Create(opts));
        p.MaxAttemptsFor(OperationType.PROVIDER_CREATE_PROCESS).Should().Be(3);
        p.MaxAttemptsFor(OperationType.DOCUMENT_DOWNLOAD).Should().Be(1);
        p.NextDelay(OperationType.PROVIDER_CREATE_PROCESS, 1, 1.0).TotalSeconds.Should().BeApproximately(7.7, 1e-6);
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData(408, ErrorClass.TRANSIENT)]
    [InlineData(429, ErrorClass.TRANSIENT)]
    [InlineData(500, ErrorClass.TRANSIENT)]
    [InlineData(502, ErrorClass.TRANSIENT)]
    [InlineData(503, ErrorClass.TRANSIENT)]
    [InlineData(504, ErrorClass.TRANSIENT)]
    [InlineData(400, ErrorClass.PERMANENT)]
    [InlineData(403, ErrorClass.PERMANENT)]
    [InlineData(404, ErrorClass.PERMANENT)]
    [InlineData(410, ErrorClass.PERMANENT)]
    [InlineData(302, ErrorClass.UNKNOWN)]
    public void Http_status_classification(int status, ErrorClass expected) =>
        ErrorClassifier.FromHttpStatus(status).Should().Be(expected);

    [Fact]
    public void Exception_classification()
    {
        ErrorClassifier.Classify(new OperationException("x", transient: true)).Should().Be(ErrorClass.TRANSIENT);
        ErrorClassifier.Classify(new OperationException("x", transient: false)).Should().Be(ErrorClass.PERMANENT);
        ErrorClassifier.Classify(new ProviderException("x", transient: true)).Should().Be(ErrorClass.TRANSIENT);
        ErrorClassifier.Classify(new HttpRequestException("net")).Should().Be(ErrorClass.TRANSIENT);
        ErrorClassifier.Classify(new TimeoutException()).Should().Be(ErrorClass.TRANSIENT);
        ErrorClassifier.Classify(new IOException()).Should().Be(ErrorClass.TRANSIENT);
        ErrorClassifier.Classify(new InvalidTransitionException("business", "A", "B")).Should().Be(ErrorClass.PERMANENT);
        ErrorClassifier.Classify(new ValidationException([new FieldError("f", "m")])).Should().Be(ErrorClass.PERMANENT);
        ErrorClassifier.Classify(new InvalidProgramException("boom")).Should().Be(ErrorClass.UNKNOWN);
        ErrorClassifier.Classify(new NullReferenceException()).Should().Be(ErrorClass.UNKNOWN);
    }

    [Fact]
    public void Database_failures_are_infrastructure_not_operation_failures()
    {
        ErrorClassifier.IsInfrastructure(new Microsoft.EntityFrameworkCore.DbUpdateException("x")).Should().BeTrue();
        ErrorClassifier.IsInfrastructure(new InvalidOperationException()).Should().BeFalse();
    }
}

public class OperationDomainsTests
{
    [Theory]
    [InlineData(OperationType.DOCUMENT_DOWNLOAD, "artifact")]
    [InlineData(OperationType.DOCUMENT_STORE, "artifact")]
    [InlineData(OperationType.SIGNED_DOCUMENT_DOWNLOAD, "artifact")]
    [InlineData(OperationType.SIGNED_DOCUMENT_STORE, "artifact")]
    [InlineData(OperationType.PROVIDER_CREATE_PROCESS, "signature-provider")]
    [InlineData(OperationType.PROVIDER_SEND_DOCUMENT, "signature-provider")]
    [InlineData(OperationType.PROVIDER_STATUS_CHECK, "signature-provider")]
    [InlineData(OperationType.IDENTITY_VALIDATION, "identity-proofing")]
    [InlineData(OperationType.CALLBACK_SEND, "callback")]
    [InlineData(OperationType.OUTPUT_DELIVERY, "callback")]
    public void Operation_maps_to_its_domain_queue_and_dlq(OperationType type, string domain)
    {
        OperationDomains.DomainOf(type).Should().Be(domain);
        OperationDomains.QueueFor(type).Should().Be(domain);
        OperationDomains.DlqFor(domain).Should().Be(domain + "-dlq");
    }

    [Fact]
    public void Every_operation_type_has_a_known_domain() =>
        Enum.GetValues<OperationType>().Select(OperationDomains.DomainOf).Should().OnlyContain(d => OperationDomains.All.Contains(d));
}
