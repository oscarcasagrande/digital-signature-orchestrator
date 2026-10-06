using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Identity;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class ProofingApi
{
    public const string Cpf = "12345678909";

    public static string Body(string externalId, string validations, string? subjectExtra = null, string cpf = Cpf) =>
        "{\"externalId\":\"" + externalId + "\",\"subject\":{\"name\":\"Maria Souza\",\"document\":\"" + cpf + "\"" + (subjectExtra ?? "") + "},"
        + "\"validations\":" + validations + "}";

    public static async Task<HttpResponseMessage> CreateSessionRawAsync(this TestFixture f, string key, string body) =>
        await f.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/proofing-sessions")
        {
            Headers = { { "Idempotency-Key", key } },
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    public static async Task<string> CreateSessionAsync(this TestFixture f, string validations, string? externalId = null, string? subjectExtra = null)
    {
        var r = await f.CreateSessionRawAsync(Guid.NewGuid().ToString(), Body(externalId ?? "KYC-" + Guid.NewGuid().ToString("N")[..8], validations, subjectExtra));
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
    }

    public static async Task<HttpResponseMessage> EvidenceAsync(this TestFixture f, string id, string endpoint, string type, string text,
        string contentType = "image/jpeg") =>
        await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{id}/{endpoint}",
            new { type, contentType, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });

    public static Task<HttpResponseMessage> SelfieAsync(this TestFixture f, string id, string text = "selfie-bytes") => f.EvidenceAsync(id, "biometrics", "SELFIE", text);
    public static Task<HttpResponseMessage> FrontAsync(this TestFixture f, string id, string text = "document-front-bytes") => f.EvidenceAsync(id, "documents", "DOCUMENT_FRONT", text);

    public static async Task<JsonElement> ResultAsync(this TestFixture f, string id) =>
        await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}/result");

    public static async Task<JsonElement> WaitForResultAsync(this TestFixture f, string id, string sessionStatus, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await f.ResultAsync(id);
            if (last.GetProperty("status").GetString() == sessionStatus) return last;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Session {id} did not reach {sessionStatus}; last={last}");
    }

    public static async Task<JsonElement> WaitForValidationAsync(this TestFixture f, string id, string capability, string status, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await f.ResultAsync(id);
            var v = last.GetProperty("validations").EnumerateArray().FirstOrDefault(x => x.GetProperty("type").GetString() == capability);
            if (v.ValueKind == JsonValueKind.Object && v.GetProperty("status").GetString() == status) return v;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Validation {capability} of {id} did not reach {status}; last={last}");
    }

    public static JsonElement Validation(this JsonElement result, string capability) =>
        result.GetProperty("validations").EnumerateArray().Single(v => v.GetProperty("type").GetString() == capability);

    public static async Task<JsonElement> WaitForSessionOperationAsync(this TestFixture f, string id, string status, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var op = (await f.SessionOpsAsync(id)).LastOrDefault();
            if (op.ValueKind == JsonValueKind.Object && op.GetProperty("status").GetString() == status) return op;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Operation of session {id} did not reach {status}");
    }

    public static async Task<List<JsonElement>> SessionOpsAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}/operations?pageSize=200")).GetProperty("items").EnumerateArray().ToList();

    public static async Task<List<JsonElement>> SessionEventsAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}/events?pageSize=200")).GetProperty("items").EnumerateArray().ToList();
}

[Collection("integration")]
public class ProofingSessionTests(TestFixture f)
{
    private const string PersonData = "[{\"type\":\"PERSON_DATA\",\"required\":true}]";

    [Fact]
    public async Task Session_with_only_person_data_completes_without_any_signature_process()
    {
        using var scope = f.Factory.Services.CreateScope();
        var processesBefore = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Processes.CountAsync();

        var id = await f.CreateSessionAsync(PersonData);
        var result = await f.WaitForResultAsync(id, "COMPLETED");
        result.GetProperty("result").GetString().Should().Be("APPROVED");
        var v = result.Validation("PERSON_DATA");
        v.GetProperty("status").GetString().Should().Be("PASSED");
        v.GetProperty("required").GetBoolean().Should().BeTrue();
        v.GetProperty("score").GetDouble().Should().BeInRange(0.80, 0.99);
        v.GetProperty("details").GetProperty("provider").GetString().Should().Be("SIMULATED");

        var types = (await f.SessionEventsAsync(id)).Select(e => e.GetProperty("type").GetString()).ToList();
        types.Should().ContainInOrder("PROOFING_SESSION_CREATED", "IDENTITY_VALIDATION_REQUESTED", "IDENTITY_VALIDATED", "PROOFING_SESSION_COMPLETED");

        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Processes.CountAsync()).Should().Be(processesBefore);
    }

    [Fact]
    public async Task Creation_is_idempotent_and_requires_the_key()
    {
        var key = Guid.NewGuid().ToString();
        var body = ProofingApi.Body("IDEMP-" + key[..6], PersonData);
        var r1 = await f.CreateSessionRawAsync(key, body);
        r1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString();
        var r2 = await f.CreateSessionRawAsync(key, body);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString().Should().Be(id);
        (await f.CreateSessionRawAsync(key, ProofingApi.Body("OTHER", PersonData))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var noKey = await f.Client.PostAsync("/v1/proofing-sessions", new StringContent(body, Encoding.UTF8, "application/json"));
        noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noKey.Content.ReadAsStringAsync()).Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task Concurrent_creations_with_the_same_key_create_one_session()
    {
        var key = Guid.NewGuid().ToString();
        var body = ProofingApi.Body("CONC-" + key[..6], PersonData);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => f.CreateSessionRawAsync(key, body)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(19);
        var ids = new HashSet<string>();
        foreach (var r in responses) ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!);
        ids.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("[\"TELEPATHY\"]", null, "validations[0]")]
    [InlineData("[\"PERSON_DATA\",\"PERSON_DATA\"]", null, "validations[1]")]
    [InlineData("[\"PHONE_OWNERSHIP\"]", null, "subject.phone")]
    [InlineData("[\"EMAIL_OWNERSHIP\"]", ",\"phone\":\"11999990001\"", "subject.email")]
    [InlineData("[]", null, "validations")]
    public async Task Invalid_requests_are_rejected_per_field(string validations, string? subjectExtra, string field)
    {
        var r = await f.CreateSessionRawAsync(Guid.NewGuid().ToString(), ProofingApi.Body("BAD-1", validations, subjectExtra));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain(field);
    }

    [Fact]
    public async Task Invalid_cpf_and_vendor_fields_are_rejected()
    {
        var bad = await f.CreateSessionRawAsync(Guid.NewGuid().ToString(), ProofingApi.Body("BAD-2", PersonData, cpf: "11111111111"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("subject.document");
        var vendor = await f.CreateSessionRawAsync(Guid.NewGuid().ToString(),
            "{\"externalId\":\"V\",\"provider\":\"ANY\",\"subject\":{\"name\":\"M\",\"document\":\"12345678909\"},\"validations\":[\"PERSON_DATA\"]}");
        vendor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await vendor.Content.ReadAsStringAsync()).Should().Contain("provider");
    }

    [Fact]
    public async Task Personal_data_is_masked_everywhere()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\",\"PHONE_OWNERSHIP\",\"EMAIL_OWNERSHIP\"]",
            subjectExtra: ",\"phone\":\"11999990001\",\"email\":\"maria@example.com\"");
        await f.WaitForResultAsync(id, "COMPLETED");
        var session = await f.Client.GetStringAsync($"/v1/proofing-sessions/{id}");
        var result = await f.Client.GetStringAsync($"/v1/proofing-sessions/{id}/result");
        var events = await f.Client.GetStringAsync($"/v1/proofing-sessions/{id}/events");
        foreach (var text in new[] { session, result, events })
            text.Should().NotContain("12345678909").And.NotContain("11999990001").And.NotContain("maria@example.com");
        var s = JsonDocument.Parse(session).RootElement.GetProperty("subject");
        s.GetProperty("document").GetString().Should().Be("*********09");
        s.GetProperty("phone").GetString().Should().Be("*********01");
        s.GetProperty("email").GetString().Should().Be("m***@example.com");
    }

    [Fact]
    public async Task Failed_required_validation_rejects_and_failed_optional_one_does_not()
    {
        var rejected = await f.CreateSessionAsync("[{\"type\":\"PERSON_DATA\",\"required\":true}]", subjectExtra: null);
        await f.WaitForResultAsync(rejected, "COMPLETED");

        var failsRequired = await f.CreateSessionRawAsync(Guid.NewGuid().ToString(), ProofingApi.Body("R-1", "[{\"type\":\"PERSON_DATA\"}]", cpf: "12345670000"));
        failsRequired.StatusCode.Should().Be(HttpStatusCode.BadRequest); // 12345670000 is not a valid CPF, so use the phone rule instead

        var id = await f.CreateSessionAsync("[{\"type\":\"PERSON_DATA\",\"required\":true},{\"type\":\"PHONE_OWNERSHIP\",\"required\":true}]",
            subjectExtra: ",\"phone\":\"11999990000\"");
        var r = await f.WaitForResultAsync(id, "COMPLETED");
        r.GetProperty("result").GetString().Should().Be("REJECTED");
        r.Validation("PHONE_OWNERSHIP").GetProperty("status").GetString().Should().Be("FAILED");

        var optional = await f.CreateSessionAsync("[{\"type\":\"PERSON_DATA\",\"required\":true},{\"type\":\"PHONE_OWNERSHIP\",\"required\":false}]",
            subjectExtra: ",\"phone\":\"11999990000\"");
        var ro = await f.WaitForResultAsync(optional, "COMPLETED");
        ro.GetProperty("result").GetString().Should().Be("APPROVED");
        ro.Validation("PHONE_OWNERSHIP").GetProperty("status").GetString().Should().Be("FAILED");
    }

    [Fact]
    public async Task Identity_risk_runs_after_all_other_validations_and_aggregates_them()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\",\"PHONE_OWNERSHIP\",\"IDENTITY_RISK\"]", subjectExtra: ",\"phone\":\"11999990001\"");
        var r = await f.WaitForResultAsync(id, "COMPLETED");
        r.GetProperty("result").GetString().Should().Be("APPROVED");
        var risk = r.Validation("IDENTITY_RISK");
        risk.GetProperty("status").GetString().Should().Be("PASSED");
        risk.GetProperty("details").GetProperty("considered").GetInt32().Should().Be(2);

        var validated = (await f.SessionEventsAsync(id)).Where(e => e.GetProperty("type").GetString() == "IDENTITY_VALIDATED")
            .Select(e => e.GetProperty("metadata").GetProperty("capability").GetString()).ToList();
        validated.Should().HaveCount(3);
        validated.Last().Should().Be("IDENTITY_RISK");
    }

    [Fact]
    public async Task Unknown_session_is_404_everywhere()
    {
        foreach (var path in new[] { "", "/result", "/events" })
            (await f.Client.GetAsync("/v1/proofing-sessions/prf_nope" + path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.SelfieAsync("prf_nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.FrontAsync("prf_nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.Client.DeleteAsync("/v1/proofing-sessions/prf_nope/evidence")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

[Collection("integration")]
public class ProofingEvidenceTests(TestFixture f)
{
    [Fact]
    public async Task Validations_run_as_soon_as_their_evidence_arrives()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\",\"LIVENESS\",\"FACE_MATCH\"]");
        var first = await f.WaitForResultAsync(id, "WAITING_EVIDENCE");
        first.Validation("PERSON_DATA").GetProperty("status").GetString().Should().BeOneOf("PENDING", "PASSED");
        first.Validation("LIVENESS").GetProperty("status").GetString().Should().Be("WAITING_EVIDENCE");

        var selfie = await f.SelfieAsync(id, "selfie body");
        selfie.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var receipt = await selfie.Content.ReadFromJsonAsync<JsonElement>();
        receipt.GetProperty("sha256").GetString().Should().Be(Sha.Hex(Encoding.UTF8.GetBytes("selfie body")));
        receipt.GetProperty("size").GetInt64().Should().Be(11);

        await f.WaitForValidationAsync(id, "LIVENESS", "PASSED");
        (await f.ResultAsync(id)).Validation("FACE_MATCH").GetProperty("status").GetString().Should().Be("WAITING_EVIDENCE"); // needs the document too
        (await f.ResultAsync(id)).GetProperty("status").GetString().Should().Be("WAITING_EVIDENCE");

        (await f.FrontAsync(id)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var done = await f.WaitForResultAsync(id, "COMPLETED");
        done.GetProperty("result").GetString().Should().Be("APPROVED");
        done.Validation("FACE_MATCH").GetProperty("status").GetString().Should().Be("PASSED");

        var session = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}");
        var evidence = session.GetProperty("evidence").EnumerateArray().ToList();
        evidence.Select(e => e.GetProperty("type").GetString()).Should().BeEquivalentTo("SELFIE", "DOCUMENT_FRONT");
        evidence.Single(e => e.GetProperty("type").GetString() == "DOCUMENT_FRONT").GetProperty("sha256").GetString()
            .Should().Be(Sha.Hex(Encoding.UTF8.GetBytes("document-front-bytes")));
        session.ToString().Should().NotContain("selfie body").And.NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("selfie body")));
    }

    [Fact]
    public async Task Provider_markers_fail_validations()
    {
        var spoof = await f.CreateSessionAsync("[\"LIVENESS\"]");
        (await f.SelfieAsync(spoof, "selfie FAKE_SPOOF")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var r = await f.WaitForResultAsync(spoof, "COMPLETED");
        r.GetProperty("result").GetString().Should().Be("REJECTED");
        r.Validation("LIVENESS").GetProperty("score").GetDouble().Should().BeLessThan(0.3);

        var mismatch = await f.CreateSessionAsync("[{\"type\":\"LIVENESS\"},{\"type\":\"FACE_MATCH\",\"required\":false}]");
        await f.SelfieAsync(mismatch, "FAKE_FACE_MISMATCH");
        await f.FrontAsync(mismatch);
        var m = await f.WaitForResultAsync(mismatch, "COMPLETED");
        m.GetProperty("result").GetString().Should().Be("APPROVED");
        m.Validation("FACE_MATCH").GetProperty("status").GetString().Should().Be("FAILED");

        var forged = await f.CreateSessionAsync("[\"DOCUMENT_AUTHENTICITY\",\"DOCUMENT_OWNERSHIP\"]");
        await f.FrontAsync(forged, "FAKE_FORGED");
        var fr = await f.WaitForResultAsync(forged, "COMPLETED");
        fr.Validation("DOCUMENT_AUTHENTICITY").GetProperty("status").GetString().Should().Be("FAILED");
        fr.Validation("DOCUMENT_OWNERSHIP").GetProperty("status").GetString().Should().Be("PASSED");
        fr.GetProperty("result").GetString().Should().Be("REJECTED");
    }

    [Fact]
    public async Task Evidence_is_validated()
    {
        var id = await f.CreateSessionAsync("[\"LIVENESS\"]");
        (await f.EvidenceAsync(id, "biometrics", "SELFIE", "x", "image/gif")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.EvidenceAsync(id, "documents", "SELFIE", "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.EvidenceAsync(id, "biometrics", "DOCUMENT_FRONT", "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{id}/biometrics", new { type = "SELFIE", contentType = "image/png", content = "!!not-base64!!" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{id}/biometrics", new { type = "SELFIE", contentType = "image/png", content = "" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var tooBig = await f.EvidenceAsync(id, "biometrics", "SELFIE", new string('x', 4096));
        tooBig.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooBig.Content.ReadAsStringAsync()).Should().Contain("limit");
        (await f.Client.PostAsync($"/v1/proofing-sessions/{id}/biometrics", new StringContent("not json", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // nothing was stored by the rejected requests
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}")).GetProperty("evidence").GetArrayLength().Should().Be(0);
        (await f.SelfieAsync(id)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Duplicate_evidence_conflicts_and_late_evidence_for_a_completed_session_conflicts()
    {
        var id = await f.CreateSessionAsync("[\"LIVENESS\"]");
        (await f.SelfieAsync(id, "first")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await f.SelfieAsync(id, "second")).StatusCode.Should().BeOneOf(HttpStatusCode.Conflict);
        await f.WaitForResultAsync(id, "COMPLETED");
        (await f.FrontAsync(id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var session = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}");
        session.GetProperty("evidence")[0].GetProperty("sha256").GetString().Should().Be(Sha.Hex(Encoding.UTF8.GetBytes("first")));
    }

    [Fact]
    public async Task Parallel_uploads_of_the_same_evidence_are_accepted_once()
    {
        var id = await f.CreateSessionAsync("[\"LIVENESS\",\"FACE_MATCH\"]");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => f.SelfieAsync(id)).Append(f.FrontAsync(id)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(2); // one selfie and the document
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        var done = await f.WaitForResultAsync(id, "COMPLETED");
        done.GetProperty("result").GetString().Should().Be("APPROVED");

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Operations.CountAsync(o => o.ProcessId == id)).Should().Be(2); // exactly one operation per validation
        (await db.Artifacts.CountAsync(a => a.ProcessId == id)).Should().Be(2);
    }

    [Fact]
    public async Task Six_concurrent_validations_close_the_session_exactly_once()
    {
        var id = await f.CreateSessionAsync(
            "[\"PERSON_DATA\",\"PHONE_OWNERSHIP\",\"EMAIL_OWNERSHIP\",\"DEVICE_RISK\",\"LIVENESS\",\"IDENTITY_RISK\"]",
            subjectExtra: ",\"phone\":\"11999990001\",\"email\":\"maria@example.com\",\"deviceId\":\"device-1\"");
        await f.WaitForValidationAsync(id, "DEVICE_RISK", "PASSED");
        await f.SelfieAsync(id);
        var done = await f.WaitForResultAsync(id, "COMPLETED");
        done.GetProperty("result").GetString().Should().Be("APPROVED");
        done.GetProperty("validations").EnumerateArray().Should().OnlyContain(v => v.GetProperty("status").GetString() == "PASSED");

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Operations.CountAsync(o => o.ProcessId == id)).Should().Be(6);
        var events = await f.SessionEventsAsync(id);
        events.Count(e => e.GetProperty("type").GetString() == "PROOFING_SESSION_COMPLETED").Should().Be(1);
        events.Count(e => e.GetProperty("type").GetString() == "IDENTITY_VALIDATED").Should().Be(6);
        events.Last(e => e.GetProperty("type").GetString() == "IDENTITY_VALIDATED").GetProperty("metadata").GetProperty("capability").GetString().Should().Be("IDENTITY_RISK");
    }
}

[Collection("integration")]
public class ProofingResilienceTests(TestFixture f)
{
    [Fact]
    public async Task Transient_provider_failures_recover_automatically()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\"]", externalId: "SIM-FLAKY-2-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForResultAsync(id, "COMPLETED");
        var op = (await f.SessionOpsAsync(id)).Single();
        op.GetProperty("attempt").GetInt32().Should().Be(3);
        (await f.SessionEventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "OPERATION_RETRY_SCHEDULED").Should().Be(2);
    }

    [Fact]
    public async Task Exhausted_validations_go_to_the_identity_dlq_and_keep_the_session_open_until_reprocessed()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\"]", externalId: "SIM-DOWN-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForValidationAsync(id, "PERSON_DATA", "ERROR");
        var session = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}");
        session.GetProperty("status").GetString().Should().Be("IN_PROGRESS");
        session.GetProperty("result").GetString().Should().Be("PENDING");

        var op = (await f.SessionOpsAsync(id)).Single();
        op.GetProperty("status").GetString().Should().Be("DLQ");
        op.GetProperty("attempt").GetInt32().Should().Be(4);

        var dl = (await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=identity-proofing&pageSize=200")).GetProperty("items")
            .EnumerateArray().Single(e => e.GetProperty("processId").GetString() == id);
        dl.GetProperty("queue").GetString().Should().Be("identity-proofing-dlq");
        dl.GetProperty("operationType").GetString().Should().Be("IDENTITY_VALIDATION");
        var msg = f.FindMessage("identity-proofing-dlq", b => b.Contains(id));
        msg.Should().NotBeNull();
        msg.Should().NotContain("12345678909").And.NotContain("Maria");

        // Reprocess: only the failed validation runs again with a fresh attempt budget (the cause persists, so it fails again).
        var retry = await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{id}/retry", new { reason = "operator check" });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetString().Should().Be(op.GetProperty("operationId").GetString());
        var again = await f.WaitForSessionOperationAsync(id, "DLQ");
        again.GetProperty("attempt").GetInt32().Should().Be(4);

        var events = await f.SessionEventsAsync(id);
        events.Should().ContainSingle(e => e.GetProperty("type").GetString() == "OPERATION_REPROCESS_REQUESTED");
        events.Count(e => e.GetProperty("type").GetString() == "OPERATION_DEAD_LETTERED").Should().Be(2);
        (await f.SessionOpsAsync(id)).Should().HaveCount(1); // no new operation was created
    }

    [Fact]
    public async Task Permanent_provider_failure_marks_the_validation_as_error_and_can_be_reprocessed_by_operation_id()
    {
        var id = await f.CreateSessionAsync("[\"PERSON_DATA\"]", externalId: "SIM-FAIL-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForValidationAsync(id, "PERSON_DATA", "ERROR");
        var op = (await f.SessionOpsAsync(id)).Single();
        op.GetProperty("status").GetString().Should().Be("FAILED");
        op.GetProperty("attempt").GetInt32().Should().Be(1);
        op.GetProperty("error").GetProperty("errorClass").GetString().Should().Be("PERMANENT");

        var retry = await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{id}/retry", new { operationId = op.GetProperty("operationId").GetString() });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForValidationAsync(id, "PERSON_DATA", "ERROR");
        (await f.SessionEventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "OPERATION_FAILED").Should().Be(2);
    }

    [Fact]
    public async Task Retry_conflicts_and_not_found()
    {
        (await f.Client.PostAsJsonAsync("/v1/proofing-sessions/prf_nope/retry", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var done = await f.CreateSessionAsync("[\"PERSON_DATA\"]");
        await f.WaitForResultAsync(done, "COMPLETED");
        (await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{done}/retry", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var waiting = await f.CreateSessionAsync("[\"LIVENESS\"]");
        (await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{waiting}/retry", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict); // nothing failed
        (await f.Client.PostAsJsonAsync($"/v1/proofing-sessions/{waiting}/retry", new { operationId = "op_nope" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

[Collection("integration")]
public class ProofingRetentionTests(TestFixture f)
{
    private async Task<string> CompletedSessionWithEvidenceAsync()
    {
        var id = await f.CreateSessionAsync("[\"LIVENESS\",\"FACE_MATCH\"]");
        await f.SelfieAsync(id, "private selfie");
        await f.FrontAsync(id, "private document");
        await f.WaitForResultAsync(id, "COMPLETED");
        return id;
    }

    [Fact]
    public async Task Manual_deletion_removes_objects_and_records_but_keeps_the_result()
    {
        var id = await CompletedSessionWithEvidenceAsync();
        using var scope = f.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        (await store.ExistsAsync($"{id}/identity/selfie", default)).Should().BeTrue();

        (await f.Client.DeleteAsync($"/v1/proofing-sessions/{id}/evidence")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await store.ExistsAsync($"{id}/identity/selfie", default)).Should().BeFalse();
        (await store.ExistsAsync($"{id}/identity/document-front", default)).Should().BeFalse();
        var session = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{id}");
        session.GetProperty("evidence").GetArrayLength().Should().Be(0);
        session.GetProperty("evidenceDeletedAt").ValueKind.Should().Be(JsonValueKind.String);
        (await f.ResultAsync(id)).GetProperty("result").GetString().Should().Be("APPROVED");

        var deleted = (await f.SessionEventsAsync(id)).Single(e => e.GetProperty("type").GetString() == "EVIDENCE_DELETED");
        deleted.GetProperty("metadata").GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("sha256").GetString())
            .Should().BeEquivalentTo(Sha.Hex(Encoding.UTF8.GetBytes("private selfie")), Sha.Hex(Encoding.UTF8.GetBytes("private document")));
        (await f.Client.DeleteAsync($"/v1/proofing-sessions/{id}/evidence")).StatusCode.Should().Be(HttpStatusCode.NoContent); // idempotent
    }

    [Fact]
    public async Task Deletion_requires_a_completed_session()
    {
        var id = await f.CreateSessionAsync("[\"LIVENESS\"]");
        await f.SelfieAsync(id, "FAKE_SPOOF"); // completes quickly, so use a session still waiting for evidence instead
        var waiting = await f.CreateSessionAsync("[\"FACE_MATCH\"]");
        await f.SelfieAsync(waiting);
        (await f.Client.DeleteAsync($"/v1/proofing-sessions/{waiting}/evidence")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{waiting}")).GetProperty("evidence").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Retention_policy_purges_only_old_completed_sessions()
    {
        var old = await CompletedSessionWithEvidenceAsync();
        var recent = await CompletedSessionWithEvidenceAsync();
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.ExecuteSqlRawAsync("UPDATE proofing_session SET completed_at = now() - interval '40 days' WHERE id = {0}", old);

        var purged = await scope.ServiceProvider.GetRequiredService<EvidencePurger>().PurgeExpiredAsync(default);
        purged.Should().BeGreaterThanOrEqualTo(2);

        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{old}")).GetProperty("evidence").GetArrayLength().Should().Be(0);
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/proofing-sessions/{recent}")).GetProperty("evidence").GetArrayLength().Should().Be(2);
        var ev = (await f.SessionEventsAsync(old)).Single(e => e.GetProperty("type").GetString() == "EVIDENCE_DELETED");
        ev.GetProperty("metadata").GetProperty("reason").GetString().Should().Be("retention");
        (await f.ResultAsync(old)).GetProperty("result").GetString().Should().Be("APPROVED");
    }

    [Fact]
    public async Task Evidence_cannot_be_downloaded()
    {
        var id = await CompletedSessionWithEvidenceAsync();
        (await f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/download-link", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.Client.GetAsync($"/v1/signature-processes/{id}/artifacts")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
