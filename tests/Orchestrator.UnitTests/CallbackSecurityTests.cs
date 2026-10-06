using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Providers;
using Orchestrator.Infrastructure.Callbacks;
using Orchestrator.Infrastructure.Storage;
using Xunit;

namespace Orchestrator.UnitTests;

public class CallbackSignerTests
{
    private const string Secret = "unit-test-secret-0123456789";
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static long Ts(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    [Fact]
    public void Signature_has_the_documented_format_and_is_deterministic()
    {
        var sig = CallbackSigner.Sign(Secret, 1000, """{"a":1}""");
        sig.Should().StartWith("sha256=").And.MatchRegex("^sha256=[0-9a-f]{64}$");
        sig.Should().Be(CallbackSigner.Sign(Secret, 1000, """{"a":1}"""));
    }

    [Fact]
    public void Known_vector_matches_hmac_sha256_of_timestamp_dot_body()
    {
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(Secret));
        var expected = "sha256=" + Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes("1000.body"))).ToLowerInvariant();
        CallbackSigner.Sign(Secret, 1000, "body").Should().Be(expected);
    }

    [Fact]
    public void Headers_carry_event_id_timestamp_and_signature()
    {
        var h = CallbackSigner.Headers(Secret, "evt_1", 1234, "body");
        h["X-Signature-Event-Id"].Should().Be("evt_1");
        h["X-Signature-Timestamp"].Should().Be("1234");
        h["X-Signature-Signature"].Should().Be(CallbackSigner.Sign(Secret, 1234, "body"));
    }

    [Fact]
    public void Valid_signature_verifies_within_tolerance()
    {
        var ts = Ts(Now);
        var sig = CallbackSigner.Sign(Secret, ts, "body");
        CallbackSigner.Verify(Secret, ts.ToString(), "body", sig, Now.AddSeconds(30), TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void Tampered_body_secret_signature_or_timestamp_fail()
    {
        var ts = Ts(Now);
        var sig = CallbackSigner.Sign(Secret, ts, "body");
        var tol = TimeSpan.FromMinutes(5);
        CallbackSigner.Verify(Secret, ts.ToString(), "body2", sig, Now, tol).Should().BeFalse();
        CallbackSigner.Verify("other-secret-0123456789", ts.ToString(), "body", sig, Now, tol).Should().BeFalse();
        CallbackSigner.Verify(Secret, (ts + 1).ToString(), "body", sig, Now, tol).Should().BeFalse();
        CallbackSigner.Verify(Secret, ts.ToString(), "body", sig + "0", Now, tol).Should().BeFalse();
        CallbackSigner.Verify(Secret, ts.ToString(), "body", null, Now, tol).Should().BeFalse();
        CallbackSigner.Verify(Secret, "abc", "body", sig, Now, tol).Should().BeFalse();
    }

    [Fact]
    public void Replay_outside_the_tolerance_is_rejected_in_both_directions()
    {
        var ts = Ts(Now);
        var sig = CallbackSigner.Sign(Secret, ts, "body");
        var tol = TimeSpan.FromMinutes(5);
        CallbackSigner.Verify(Secret, ts.ToString(), "body", sig, Now.AddMinutes(6), tol).Should().BeFalse();
        CallbackSigner.Verify(Secret, ts.ToString(), "body", sig, Now.AddMinutes(-6), tol).Should().BeFalse();
    }
}

public class SsrfGuardTests
{
    private static SsrfGuard Strict(string[]? allowed = null) => new(new SsrfPolicy(false, false, allowed));

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("https://cliente.exemplo.com/signatures/callback?x=1")]
    [InlineData("https://8.8.8.8/hook")]
    public void Public_https_urls_are_accepted(string url) => Strict().ValidateUrl(url).Should().BeNull();

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://app.localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://127.5.5.5/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://172.16.0.1/hook")]
    [InlineData("https://172.31.255.255/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://100.64.0.1/hook")]
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fc00::1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.1]/hook")]
    public void Unsafe_urls_are_rejected(string url) => Strict().ValidateUrl(url).Should().NotBeNull();

    [Theory]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("ff02::1", true)]
    public void Address_classification(string ip, bool blocked) => SsrfGuard.IsPrivateOrReserved(IPAddress.Parse(ip)).Should().Be(blocked);

    [Fact]
    public void Development_policy_can_allow_http_and_private_networks()
    {
        var dev = new SsrfGuard(new SsrfPolicy(true, true, null));
        dev.ValidateUrl("http://127.0.0.1:8080/hook").Should().BeNull();
        dev.IsBlocked(IPAddress.Loopback).Should().BeFalse();
    }

    [Fact]
    public void Allowlist_restricts_hosts_and_supports_wildcards()
    {
        var g = Strict(["partner.example.com", "*.corp.example.com"]);
        g.ValidateUrl("https://partner.example.com/x").Should().BeNull();
        g.ValidateUrl("https://api.corp.example.com/x").Should().BeNull();
        g.ValidateUrl("https://evil.example.org/x").Should().NotBeNull();
        g.ValidateUrl("https://corp.example.com.evil.org/x").Should().NotBeNull();
    }

    [Fact]
    public void Rate_limiter_allows_the_limit_per_second_per_host_and_recovers()
    {
        var limiter = new HostRateLimiter(Options.Create(new CallbackOptions { RateLimitPerSecond = 3 }));
        for (var i = 0; i < 3; i++) limiter.TryAcquire("a.example.com", 1000 + i).Should().BeTrue();
        limiter.TryAcquire("a.example.com", 1500).Should().BeFalse();
        limiter.TryAcquire("b.example.com", 1500).Should().BeTrue(); // other hosts are independent
        limiter.TryAcquire("a.example.com", 2100).Should().BeTrue(); // window slid
    }
}

/// <summary>Real connections against a local server prove the guard acts on the connection, not just on the URL text.</summary>
public class GuardedConnectionTests : IAsyncLifetime
{
    private WebApplication _server = default!;
    private string _url = "";

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _server = b.Build();
        _server.MapGet("/ok", () => "ok");
        _server.MapPost("/hook", () => Results.Ok());
        _server.MapPost("/redirect", () => Results.Redirect("/ok"));
        await _server.StartAsync();
        _url = _server.Urls.First();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Strict_policy_blocks_a_loopback_ip_literal_at_connection_time()
    {
        using var http = new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(false, true, null))));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(_url + "/ok"));
        ex.InnerException.Should().BeOfType<SsrfBlockedException>();
    }

    [Fact]
    public async Task Strict_policy_blocks_a_public_looking_name_that_resolves_to_loopback()
    {
        // "localhost" stands for any attacker-controlled name whose DNS answer is a private address (DNS rebinding).
        var port = new Uri(_url).Port;
        using var http = new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(false, true, null))));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{port}/ok"));
        ex.InnerException.Should().BeOfType<SsrfBlockedException>();
    }

    [Fact]
    public async Task Permissive_policy_connects()
    {
        using var http = new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(true, true, null))));
        (await http.GetStringAsync(_url + "/ok")).Should().Be("ok");
    }

    [Fact]
    public async Task Callback_sender_blocked_destination_is_a_permanent_failure()
    {
        var opts = Options.Create(new CallbackOptions());
        var sender = new HttpCallbackSender(
            new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(false, true, null)))) { Timeout = Timeout.InfiniteTimeSpan },
            new HostRateLimiter(opts), opts);
        var ex = await Assert.ThrowsAsync<CallbackDeliveryException>(() =>
            sender.SendAsync(new CallbackRequest(_url + "/hook", "{}", new Dictionary<string, string>()), default));
        ex.Transient.Should().BeFalse();
    }

    [Fact]
    public async Task Callback_sender_does_not_follow_redirects_and_treats_3xx_as_permanent()
    {
        var opts = Options.Create(new CallbackOptions());
        var sender = new HttpCallbackSender(
            new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(true, true, null)))) { Timeout = Timeout.InfiniteTimeSpan },
            new HostRateLimiter(opts), opts);
        var ex = await Assert.ThrowsAsync<CallbackDeliveryException>(() =>
            sender.SendAsync(new CallbackRequest(_url + "/redirect", "{}", new Dictionary<string, string>()), default));
        ex.StatusCode.Should().Be(302);
        ex.Transient.Should().BeFalse();
    }

    [Fact]
    public async Task Callback_sender_rate_limit_is_a_transient_failure()
    {
        var opts = Options.Create(new CallbackOptions { RateLimitPerSecond = 1 });
        var sender = new HttpCallbackSender(
            new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(true, true, null)))) { Timeout = Timeout.InfiniteTimeSpan },
            new HostRateLimiter(opts), opts);
        var req = new CallbackRequest(_url + "/hook", "{}", new Dictionary<string, string>());
        (await sender.SendAsync(req, default)).Should().Be(200);
        var ex = await Assert.ThrowsAsync<CallbackDeliveryException>(() => sender.SendAsync(req, default));
        ex.Transient.Should().BeTrue();
    }

    [Fact]
    public async Task Document_fetcher_with_strict_guard_is_blocked_permanently()
    {
        using var http = new HttpClient(GuardedConnect.CreateHandler(new SsrfGuard(new SsrfPolicy(false, true, null)), maxRedirects: 3));
        var fetcher = new HttpDocumentFetcher(http);
        var ex = await Assert.ThrowsAsync<OperationException>(() => fetcher.FetchAsync(new Uri(_url + "/ok"), 1000, default));
        ex.Transient.Should().BeFalse();
    }
}
