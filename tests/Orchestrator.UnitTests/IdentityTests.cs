using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.UnitTests;

public class IdentityCapabilitiesTests
{
    private static ProofingRequestValidatorResult Parse(string json)
    {
        var (req, errors) = ProofingRequestValidator.Parse(JsonNode.Parse(json));
        return new ProofingRequestValidatorResult(req, errors);
    }

    private sealed record ProofingRequestValidatorResult(ProofingRequest? Request, List<Orchestrator.Application.Processes.FieldError> Errors);

    private const string Valid = """
    {"externalId":"KYC-1","subject":{"name":"Maria","document":"12345678909"},"validations":["PERSON_DATA",{"type":"LIVENESS","required":false}]}
    """;

    [Fact]
    public void Catalog_contains_the_eleven_prd_capabilities() =>
        IdentityCapabilities.All.Should().BeEquivalentTo("PERSON_DATA", "DOCUMENT_DATA", "DOCUMENT_AUTHENTICITY", "DOCUMENT_OWNERSHIP", "FACE_MATCH",
            "LIVENESS", "GOVERNMENT_BIOMETRIC_MATCH", "PHONE_OWNERSHIP", "EMAIL_OWNERSHIP", "DEVICE_RISK", "IDENTITY_RISK");

    [Theory]
    [InlineData("PERSON_DATA", new string[0])]
    [InlineData("DOCUMENT_DATA", new[] { "DOCUMENT_FRONT" })]
    [InlineData("DOCUMENT_AUTHENTICITY", new[] { "DOCUMENT_FRONT" })]
    [InlineData("DOCUMENT_OWNERSHIP", new[] { "DOCUMENT_FRONT" })]
    [InlineData("LIVENESS", new[] { "SELFIE" })]
    [InlineData("GOVERNMENT_BIOMETRIC_MATCH", new[] { "SELFIE" })]
    [InlineData("FACE_MATCH", new[] { "SELFIE", "DOCUMENT_FRONT" })]
    [InlineData("PHONE_OWNERSHIP", new string[0])]
    [InlineData("EMAIL_OWNERSHIP", new string[0])]
    [InlineData("DEVICE_RISK", new string[0])]
    [InlineData("IDENTITY_RISK", new string[0])]
    public void Evidence_prerequisites(string capability, string[] expected) =>
        IdentityCapabilities.EvidenceFor(capability).Select(t => t.ToString()).Should().BeEquivalentTo(expected);

    [Fact]
    public void Readiness_depends_on_evidence_and_for_risk_on_the_other_validations()
    {
        var none = new HashSet<ArtifactType>();
        var selfie = new HashSet<ArtifactType> { ArtifactType.SELFIE };
        var both = new HashSet<ArtifactType> { ArtifactType.SELFIE, ArtifactType.DOCUMENT_FRONT };
        IdentityCapabilities.IsReady("PERSON_DATA", none, []).Should().BeTrue();
        IdentityCapabilities.IsReady("LIVENESS", none, []).Should().BeFalse();
        IdentityCapabilities.IsReady("LIVENESS", selfie, []).Should().BeTrue();
        IdentityCapabilities.IsReady("FACE_MATCH", selfie, []).Should().BeFalse();
        IdentityCapabilities.IsReady("FACE_MATCH", both, []).Should().BeTrue();

        var vs = new List<IdentityValidation>
        {
            new() { Capability = "PERSON_DATA", Status = ValidationStatus.PASSED },
            new() { Capability = "LIVENESS", Status = ValidationStatus.PENDING },
            new() { Capability = "IDENTITY_RISK", Status = ValidationStatus.WAITING_EVIDENCE }
        };
        IdentityCapabilities.IsReady("IDENTITY_RISK", none, vs).Should().BeFalse();
        vs[1].Status = ValidationStatus.FAILED;
        IdentityCapabilities.IsReady("IDENTITY_RISK", none, vs).Should().BeTrue();
        vs[1].Status = ValidationStatus.ERROR;
        IdentityCapabilities.IsReady("IDENTITY_RISK", none, vs).Should().BeFalse();
    }

    [Fact]
    public void Valid_request_is_parsed_with_required_defaulting_to_true()
    {
        var r = Parse(Valid);
        r.Errors.Should().BeEmpty();
        r.Request!.Validations.Should().BeEquivalentTo([new RequestedValidation("PERSON_DATA", true), new RequestedValidation("LIVENESS", false)]);
        r.Request.Subject.Document.Should().Be("12345678909");
    }

    [Theory]
    [InlineData("""{"subject":{"name":"M","document":"12345678909"},"validations":["PERSON_DATA"]}""", "externalId")]
    [InlineData("""{"externalId":"x","validations":["PERSON_DATA"]}""", "subject")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"11111111111"},"validations":["PERSON_DATA"]}""", "subject.document")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":[]}""", "validations")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":["TELEPATHY"]}""", "validations[0]")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":["LIVENESS","LIVENESS"]}""", "validations[1]")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":["PHONE_OWNERSHIP"]}""", "subject.phone")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":["EMAIL_OWNERSHIP"]}""", "subject.email")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909"},"validations":["DEVICE_RISK"]}""", "subject.deviceId")]
    [InlineData("""{"externalId":"x","provider":"ANY","subject":{"name":"M","document":"12345678909"},"validations":["PERSON_DATA"]}""", "provider")]
    [InlineData("""{"externalId":"x","subject":{"name":"M","document":"12345678909","phone":"abc"},"validations":["PERSON_DATA"]}""", "subject.phone")]
    public void Invalid_requests_report_the_field(string json, string field) =>
        Parse(json).Errors.Should().Contain(e => e.Field == field);

    [Fact]
    public void Subject_data_required_by_capabilities_is_accepted_when_present()
    {
        var r = Parse("""
        {"externalId":"x","subject":{"name":"M","document":"12345678909","phone":"11999990001","email":"m@example.com","deviceId":"dev-1"},
         "validations":["PHONE_OWNERSHIP","EMAIL_OWNERSHIP","DEVICE_RISK"]}
        """);
        r.Errors.Should().BeEmpty();
    }
}

public class FakeIdentityAdapterTests
{
    private static readonly FakeIdentityAdapter Adapter = new();

    private static IdentityValidationRequest Req(string capability, string externalId = "KYC-1", string document = "12345678909",
        string? phone = null, string? email = null, string? deviceId = null, Dictionary<ArtifactType, byte[]>? evidence = null,
        IReadOnlyList<PriorResult>? prior = null, int attempt = 1) =>
        new("prf_1", externalId, capability, new ProofingSubject("Maria", document, phone, email, deviceId),
            evidence ?? new(), prior ?? [], attempt);

    private static Dictionary<ArtifactType, byte[]> Selfie(string text) => new() { [ArtifactType.SELFIE] = Encoding.UTF8.GetBytes(text) };
    private static Dictionary<ArtifactType, byte[]> Front(string text) => new() { [ArtifactType.DOCUMENT_FRONT] = Encoding.UTF8.GetBytes(text) };

    [Theory]
    [InlineData("LIVENESS", "FAKE_SPOOF")]
    [InlineData("FACE_MATCH", "FAKE_FACE_MISMATCH")]
    [InlineData("GOVERNMENT_BIOMETRIC_MATCH", "FAKE_NO_MATCH")]
    public async Task Selfie_markers_fail_the_matching_capability_only(string capability, string marker)
    {
        (await Adapter.ValidateAsync(Req(capability, evidence: Selfie("img " + marker)), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req(capability, evidence: Selfie("img clean")), default)).Passed.Should().BeTrue();
        // a marker only affects its own capability: the other selfie-based capability still passes
        var other = capability == "LIVENESS" ? "FACE_MATCH" : "LIVENESS";
        (await Adapter.ValidateAsync(Req(other, evidence: Selfie("img " + marker)), default)).Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("DOCUMENT_AUTHENTICITY", "FAKE_FORGED")]
    [InlineData("DOCUMENT_OWNERSHIP", "FAKE_NOT_OWNER")]
    public async Task Document_markers_fail_the_matching_capability(string capability, string marker)
    {
        (await Adapter.ValidateAsync(Req(capability, evidence: Front(marker)), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req(capability, evidence: Front("ok")), default)).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Subject_data_rules()
    {
        (await Adapter.ValidateAsync(Req("PERSON_DATA", document: "12345670000"), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req("PERSON_DATA"), default)).Passed.Should().BeTrue();
        (await Adapter.ValidateAsync(Req("PHONE_OWNERSHIP", phone: "11999990000"), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req("PHONE_OWNERSHIP", phone: "11999990001"), default)).Passed.Should().BeTrue();
        (await Adapter.ValidateAsync(Req("EMAIL_OWNERSHIP", email: "x@fraud.example"), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req("EMAIL_OWNERSHIP", email: "x@example.com"), default)).Passed.Should().BeTrue();
        (await Adapter.ValidateAsync(Req("DEVICE_RISK", deviceId: "risky-1"), default)).Passed.Should().BeFalse();
        (await Adapter.ValidateAsync(Req("DEVICE_RISK", deviceId: "dev-1"), default)).Passed.Should().BeTrue();
        (await Adapter.ValidateAsync(Req("DOCUMENT_DATA", evidence: Front("x")), default)).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Scores_are_deterministic_and_in_range()
    {
        var pass = await Adapter.ValidateAsync(Req("PERSON_DATA"), default);
        var again = await Adapter.ValidateAsync(Req("PERSON_DATA"), default);
        pass.Score.Should().Be(again.Score).And.BeInRange(0.80, 0.99);
        var fail = await Adapter.ValidateAsync(Req("PERSON_DATA", document: "12345670000"), default);
        fail.Score.Should().BeInRange(0.0, 0.29);
    }

    [Fact]
    public async Task Details_contain_no_personal_data()
    {
        var r = await Adapter.ValidateAsync(Req("PERSON_DATA", document: "12345670000"), default);
        r.DetailsJson.Should().NotContain("12345670000").And.NotContain("Maria");
        JsonDocument.Parse(r.DetailsJson).RootElement.GetProperty("provider").GetString().Should().Be("SIMULATED");
    }

    [Fact]
    public async Task Identity_risk_aggregates_prior_scores()
    {
        var good = await Adapter.ValidateAsync(Req("IDENTITY_RISK", prior: [new("A", true, 0.9), new("B", true, 0.9)]), default);
        good.Passed.Should().BeTrue();
        good.Score.Should().BeApproximately(0.9, 1e-6);

        var bad = await Adapter.ValidateAsync(Req("IDENTITY_RISK", prior: [new("A", true, 0.9), new("B", false, 0.1), new("C", false, 0.0)]), default);
        bad.Passed.Should().BeFalse(); // risk = 1 - 0.3 = 0.7
        JsonDocument.Parse(bad.DetailsJson).RootElement.GetProperty("failed").GetInt32().Should().Be(2);

        (await Adapter.ValidateAsync(Req("IDENTITY_RISK"), default)).Passed.Should().BeTrue(); // nothing to aggregate
    }

    [Fact]
    public async Task Failure_injection_by_external_id()
    {
        var flaky = () => Adapter.ValidateAsync(Req("PERSON_DATA", externalId: "SIM-FLAKY-2-x", attempt: 2), default);
        (await flaky.Should().ThrowAsync<ProviderException>()).Which.Transient.Should().BeTrue();
        (await Adapter.ValidateAsync(Req("PERSON_DATA", externalId: "SIM-FLAKY-2-x", attempt: 3), default)).Passed.Should().BeTrue();

        var down = () => Adapter.ValidateAsync(Req("PERSON_DATA", externalId: "SIM-DOWN-x"), default);
        (await down.Should().ThrowAsync<ProviderException>()).Which.Transient.Should().BeTrue();
        var fail = () => Adapter.ValidateAsync(Req("PERSON_DATA", externalId: "SIM-FAIL-x"), default);
        (await fail.Should().ThrowAsync<ProviderException>()).Which.Transient.Should().BeFalse();
        var unknown = () => Adapter.ValidateAsync(Req("PERSON_DATA", externalId: "SIM-UNKNOWN-x"), default);
        await unknown.Should().ThrowAsync<InvalidProgramException>();
    }

    [Fact]
    public void Adapter_contract_exposes_no_vendor_types() =>
        typeof(IIdentityProofingAdapter).GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType.Namespace))
            .Should().OnlyContain(ns => ns == null || ns.StartsWith("System") || ns.StartsWith("Orchestrator.Application"));
}

public class ProofingAggregationTests
{
    private static (ProofingCore core, OrchestratorDbContext db) Create()
    {
        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new FixedClock(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        return (new ProofingCore(db, new EventRecorder(db, clock), clock, new RetryPolicy(Options.Create(new RetryOptions()))), db);
    }

    private static ProofingSession Session(params (string cap, bool required, ValidationStatus status)[] vs) => new()
    {
        Id = "prf_1", ExternalId = "x", CorrelationId = "c",
        Validations = vs.Select(v => new IdentityValidation { Id = "ivl_" + v.cap, SessionId = "prf_1", Capability = v.cap, Required = v.required, Status = v.status }).ToList()
    };

    [Fact]
    public void All_required_passed_is_approved_and_completes_the_session()
    {
        var (core, db) = Create();
        var s = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("LIVENESS", true, ValidationStatus.PASSED));
        core.Recompute(s, "c", null);
        s.Status.Should().Be(ProofingSessionStatus.COMPLETED);
        s.Result.Should().Be(ProofingResult.APPROVED);
        s.CompletedAt.Should().NotBeNull();
        db.Journal.Local.Should().ContainSingle(e => e.Type == "PROOFING_SESSION_COMPLETED");
    }

    [Fact]
    public void A_failed_required_validation_rejects_but_a_failed_optional_one_does_not()
    {
        var (core, _) = Create();
        var rejected = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("LIVENESS", true, ValidationStatus.FAILED));
        core.Recompute(rejected, "c", null);
        rejected.Result.Should().Be(ProofingResult.REJECTED);

        var approved = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("FACE_MATCH", false, ValidationStatus.FAILED));
        core.Recompute(approved, "c", null);
        approved.Result.Should().Be(ProofingResult.APPROVED);
    }

    [Fact]
    public void Pending_or_error_validations_keep_the_session_open()
    {
        var (core, _) = Create();
        var waiting = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("LIVENESS", true, ValidationStatus.WAITING_EVIDENCE));
        core.Recompute(waiting, "c", null);
        (waiting.Status, waiting.Result).Should().Be((ProofingSessionStatus.WAITING_EVIDENCE, ProofingResult.PENDING));

        var running = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("LIVENESS", true, ValidationStatus.PENDING));
        core.Recompute(running, "c", null);
        running.Status.Should().Be(ProofingSessionStatus.IN_PROGRESS);

        var error = Session(("PERSON_DATA", true, ValidationStatus.PASSED), ("LIVENESS", true, ValidationStatus.ERROR));
        core.Recompute(error, "c", null);
        (error.Status, error.Result).Should().Be((ProofingSessionStatus.IN_PROGRESS, ProofingResult.PENDING));
    }

    [Fact]
    public void Risk_waiting_for_dependencies_does_not_count_as_waiting_for_evidence()
    {
        var (core, _) = Create();
        var s = Session(("LIVENESS", true, ValidationStatus.PENDING), ("IDENTITY_RISK", true, ValidationStatus.WAITING_EVIDENCE));
        core.Recompute(s, "c", null);
        s.Status.Should().Be(ProofingSessionStatus.IN_PROGRESS);
    }

    [Fact]
    public async Task Operations_are_created_once_for_validations_whose_prerequisites_are_met()
    {
        var (core, db) = Create();
        var s = Session(("PERSON_DATA", true, ValidationStatus.WAITING_EVIDENCE), ("LIVENESS", true, ValidationStatus.WAITING_EVIDENCE),
            ("IDENTITY_RISK", true, ValidationStatus.WAITING_EVIDENCE));
        db.ProofingSessions.Add(s);
        await core.CreateReadyOperationsAsync(s, "c", null, default);
        await db.SaveChangesAsync();

        s.Validations.Single(v => v.Capability == "PERSON_DATA").Status.Should().Be(ValidationStatus.PENDING);
        s.Validations.Single(v => v.Capability == "LIVENESS").Status.Should().Be(ValidationStatus.WAITING_EVIDENCE); // needs the selfie
        s.Validations.Single(v => v.Capability == "IDENTITY_RISK").Status.Should().Be(ValidationStatus.WAITING_EVIDENCE);
        (await db.Operations.CountAsync()).Should().Be(1);
        (await db.Outbox.CountAsync(o => o.Queue == "identity-proofing")).Should().Be(1);

        await core.CreateReadyOperationsAsync(s, "c", null, default); // idempotent
        await db.SaveChangesAsync();
        (await db.Operations.CountAsync()).Should().Be(1);
    }
}
