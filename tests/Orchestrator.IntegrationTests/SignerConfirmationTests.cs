using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

public sealed record SinkCode(string SignerId, string Channel, string Destination, string Code);

[Collection("integration")]
public class SignerConfirmationTests(TestFixture f)
{
    private const string Cpf = "12345678909";

    private static string Signer(string name, string? email = null, string? phone = null, int? order = null, string? confirmation = null,
        string? type = null, string? externalId = null) =>
        "{\"name\":\"" + name + "\",\"document\":\"" + Cpf + "\""
        + (email is null ? "" : ",\"email\":\"" + email + "\"") + (phone is null ? "" : ",\"phone\":\"" + phone + "\"")
        + (order is null ? "" : ",\"order\":" + order) + (confirmation is null ? "" : ",\"confirmation\":" + confirmation)
        + (type is null ? "" : ",\"signatureType\":\"" + type + "\"") + (externalId is null ? "" : ",\"externalId\":\"" + externalId + "\"") + "}";

    private string Body(string signers, string? defaults = null, string? callback = null, string? source = null, string? externalId = null) =>
        "{\"externalId\":\"" + (externalId ?? "SC-" + Guid.NewGuid().ToString("N")[..8]) + "\","
        + "\"document\":{\"fileName\":\"c.pdf\",\"source\":" + (source ?? "{\"type\":\"URL\",\"url\":\"" + TestFixture.DocumentUrl + "\"}") + "},"
        + "\"signers\":[" + signers + "]"
        + (defaults is null ? "" : ",\"defaults\":" + defaults) + (callback is null ? "" : ",\"callback\":" + callback) + "}";

    private async Task<string> Create(string signers, string? defaults = null, string? callback = null, string? source = null, string? externalId = null)
    {
        var r = await f.CreateAsync(Guid.NewGuid().ToString(), Body(signers, defaults, callback, source, externalId));
        (await r.Content.ReadAsStringAsync()).Should().NotBeNull();
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString()!;
    }

    private Task<JsonElement> Detail(string id) => f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");

    private async Task<List<SinkCode>> Sink(string id)
    {
        var rows = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/dev/confirmation-codes/{id}");
        return rows.EnumerateArray().Select(r => new SinkCode(r.GetProperty("signerId").GetString()!, r.GetProperty("channel").GetString()!,
            r.GetProperty("destination").GetString()!, r.GetProperty("code").GetString()!)).ToList();
    }

    private async Task<List<SinkCode>> WaitCodes(string id, int count, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        List<SinkCode> codes;
        do { codes = await Sink(id); if (codes.Count >= count) return codes; await Task.Delay(250); } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Expected {count} codes for {id}, got {codes.Count}");
    }

    private Task<HttpResponseMessage> Confirm(string id, string signerId, string channel, string code, HttpClient? client = null) =>
        (client ?? f.Client).PostAsJsonAsync($"/v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/confirm", new { code });

    private Task<HttpResponseMessage> Resend(string id, string signerId, string channel, HttpClient? client = null) =>
        (client ?? f.Client).PostAsync($"/v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/resend", null);

    private static JsonElement SignerOf(JsonElement detail, int index) => detail.GetProperty("signers")[index];

    private async Task<string> SignerId(string id, int index) => SignerOf(await Detail(id), index).GetProperty("id").GetString()!;

    private static string Wrong(string code) => code == "000000" ? "111111" : "000000";

    private async Task<List<string>> EventTypes(string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=200")).GetProperty("items")
            .EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();

    // ---- creation ----------------------------------------------------------------------------------

    [Fact]
    public async Task Defaults_are_inherited_and_contacts_are_masked()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com") + "," + Signer("Bia", email: "bia@example.com", phone: "+5511999990000", confirmation: "[\"SMS\"]", type: "QUALIFIED"),
            defaults: "{\"signatureType\":\"ADVANCED\",\"confirmation\":[\"EMAIL\"]}");
        var d = await Detail(id);
        var a = SignerOf(d, 0);
        var b = SignerOf(d, 1);
        a.GetProperty("signatureType").GetString().Should().Be("ADVANCED");
        a.GetProperty("confirmations").EnumerateArray().Select(c => c.GetProperty("channel").GetString()).Should().Equal("EMAIL");
        a.GetProperty("email").GetString().Should().Be("a***@example.com");
        b.GetProperty("signatureType").GetString().Should().Be("QUALIFIED");
        b.GetProperty("confirmations").EnumerateArray().Select(c => c.GetProperty("channel").GetString()).Should().Equal("SMS");
        b.GetProperty("phone").GetString().Should().NotContain("99999").And.EndWith("00");
        d.ToString().Should().NotContain("ana@example.com").And.NotContain("+5511999990000");
        d.GetProperty("progress").GetProperty("totalSteps").GetInt32().Should().Be(1 + 2 + 2 + 1);
    }

    [Fact]
    public async Task Invalid_signers_are_reported_with_signer_and_field()
    {
        var r = await f.CreateAsync(Guid.NewGuid().ToString(), Body(
            Signer("Ana", email: "ana@example.com", confirmation: "[\"EMAIL\"]", type: "SIMPLE") + ","
            + Signer("Bia", confirmation: "[\"SMS\"]", type: "SIMPLE") + ","
            + Signer("Cris", type: "SIMPLE", order: 3)));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        errors.TryGetProperty("signers[1].phone", out _).Should().BeTrue(errors.ToString());
        errors.TryGetProperty("signers[2].order", out _).Should().BeTrue(errors.ToString());
        errors.TryGetProperty("signers[0].email", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_payload_is_still_accepted_without_confirmation_and_in_parallel()
    {
        var id = await f.CreateProcessAsync(signers: 2);
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await Sink(id)).Should().BeEmpty();
        var d = await Detail(id);
        d.GetProperty("signers").EnumerateArray().Should().OnlyContain(s => s.GetProperty("confirmations").GetArrayLength() == 0);
        var p = d.GetProperty("progress");
        (p.GetProperty("completedSteps").GetInt32(), p.GetProperty("totalSteps").GetInt32()).Should().Be((p.GetProperty("totalSteps").GetInt32(), 1 + 2 + 1));
    }

    // ---- confirmation ------------------------------------------------------------------------------

    [Fact]
    public async Task Nobody_signs_before_every_channel_is_confirmed_and_then_the_process_completes()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com", phone: "+5511999990000"),
            defaults: "{\"signatureType\":\"ADVANCED\",\"confirmation\":[\"EMAIL\",\"SMS\"]}");
        var codes = await WaitCodes(id, 2);
        codes.Select(c => c.Channel).Should().BeEquivalentTo("EMAIL", "SMS");
        codes.Should().OnlyContain(c => Regex6(c.Code));
        var signerId = await SignerId(id, 0);

        await Task.Delay(3000); // far longer than the provider delay (0.5 s) and the poll (1 s)
        var waiting = await Detail(id);
        waiting.GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        SignerOf(waiting, 0).GetProperty("signed").GetBoolean().Should().BeFalse();
        waiting.GetProperty("progress").GetProperty("currentStep").GetProperty("kind").GetString().Should().Be("CONFIRMATION");

        var email = codes.Single(c => c.Channel == "EMAIL").Code;
        var bad = await Confirm(id, signerId, "EMAIL", Wrong(email));
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attemptsRemaining").GetInt32().Should().Be(4);

        (await Confirm(id, signerId, "EMAIL", email)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(2500);
        SignerOf(await Detail(id), 0).GetProperty("signed").GetBoolean().Should().BeFalse("SMS is still pending");

        (await Confirm(id, signerId, "SMS", codes.Single(c => c.Channel == "SMS").Code)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");

        var final = await Detail(id);
        var p = final.GetProperty("progress");
        p.GetProperty("completedSteps").GetInt32().Should().Be(p.GetProperty("totalSteps").GetInt32());
        p.GetProperty("currentStep").ValueKind.Should().Be(JsonValueKind.Null);
        (await EventTypes(id)).Should().ContainInOrder("CONFIRMATION_REQUESTED", "CONFIRMATION_SENT", "CONFIRMATION_CODE_REJECTED", "CONFIRMATION_CONFIRMED", "SIGNER_RELEASED", "SIGNER_SIGNED", "PROCESS_COMPLETED");

        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        ops.Count(o => o.GetProperty("type").GetString() == "CONFIRMATION_SEND").Should().Be(2);
        ops.Count(o => o.GetProperty("type").GetString() == "CONFIRMATION_VERIFY").Should().Be(3); // wrong + two right
        ops.Should().OnlyContain(o => o.GetProperty("status").GetString() == "COMPLETED");
    }

    private static bool Regex6(string s) => s.Length == 6 && s.All(char.IsDigit);

    [Fact]
    public async Task The_code_never_reaches_events_operations_responses_or_logs()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var code = (await WaitCodes(id, 1))[0].Code;
        var signerId = await SignerId(id, 0);
        var wrong = await Confirm(id, signerId, "EMAIL", Wrong(code));
        var ok = await Confirm(id, signerId, "EMAIL", code);
        await f.WaitForStatusAsync(id, "COMPLETED");

        var surfaces = new List<string>
        {
            await wrong.Content.ReadAsStringAsync(), await ok.Content.ReadAsStringAsync(), (await Detail(id)).ToString(),
            (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=200")).ToString(),
            (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200")).ToString(),
            (await f.Client.GetFromJsonAsync<JsonElement>("/v1/signature-processes?pageSize=200")).ToString(),
            string.Join("\n", CapturingLoggerProvider.Lines.ToArray())
        };
        var pattern = new System.Text.RegularExpressions.Regex("(?<![0-9A-Za-z])" + code + "(?![0-9A-Za-z])");
        foreach (var s in surfaces) pattern.IsMatch(s).Should().BeFalse("the one-time code must only exist in the notifier sink");

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var hashes = await db.Confirmations.Where(c => c.ProcessId == id).Select(c => c.CodeHash).ToListAsync();
        hashes.Should().OnlyContain(h => h == null); // cleared on confirmation
        (await db.Outbox.Where(o => o.AggregateId == id).Select(o => o.PayloadJson).ToListAsync()).Should().NotContain(p => pattern.IsMatch(p));
    }

    [Fact]
    public async Task Five_wrong_codes_lock_and_a_resend_issues_a_new_one()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var first = (await WaitCodes(id, 1))[0].Code;
        var signerId = await SignerId(id, 0);
        for (var i = 4; i >= 1; i--)
        {
            var r = await Confirm(id, signerId, "EMAIL", Wrong(first));
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attemptsRemaining").GetInt32().Should().Be(i);
        }
        (await Confirm(id, signerId, "EMAIL", Wrong(first))).StatusCode.Should().Be((HttpStatusCode)423);
        (await Confirm(id, signerId, "EMAIL", first)).StatusCode.Should().Be((HttpStatusCode)423, "a locked code is dead even when right");
        (await Detail(id)).GetProperty("progress").GetProperty("currentStep").GetProperty("status").GetString().Should().Be("FAILED");

        await Task.Delay(1200); // resend minimum interval in tests is 1 s
        (await Resend(id, signerId, "EMAIL")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var second = (await WaitCodes(id, 2)).Last().Code;
        (await Confirm(id, signerId, "EMAIL", second)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await EventTypes(id)).Should().Contain(["CONFIRMATION_LOCKED", "CONFIRMATION_RESEND_REQUESTED"]);
    }

    [Fact]
    public async Task Expired_code_answers_410_and_a_resend_recovers()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var code = (await WaitCodes(id, 1))[0].Code;
        var signerId = await SignerId(id, 0);
        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Confirmations.Where(c => c.ProcessId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        (await Detail(id)).GetProperty("signers")[0].GetProperty("confirmations")[0].GetProperty("status").GetString().Should().Be("EXPIRED");
        (await Confirm(id, signerId, "EMAIL", code)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await EventTypes(id)).Should().Contain("CONFIRMATION_CODE_EXPIRED");

        await Task.Delay(1200);
        (await Resend(id, signerId, "EMAIL")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var fresh = (await WaitCodes(id, 2)).Last().Code;
        (await Confirm(id, signerId, "EMAIL", fresh)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
    }

    [Fact]
    public async Task Resend_is_rate_limited_by_interval_and_by_total_sends()
    {
        using var slow = f.CreateApiClient(new() { ["Confirmation:ResendMinIntervalSeconds"] = "30" });
        var id = await Create(Signer("Ana", email: "ana@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        await WaitCodes(id, 1);
        var signerId = await SignerId(id, 0);
        var early = await Resend(id, signerId, "EMAIL", slow);
        early.StatusCode.Should().Be((HttpStatusCode)429);
        early.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 30);

        using var capped = f.CreateApiClient(new() { ["Confirmation:ResendMinIntervalSeconds"] = "0", ["Confirmation:MaxSends"] = "1" });
        var limit = await Resend(id, signerId, "EMAIL", capped);
        limit.StatusCode.Should().Be((HttpStatusCode)429);
        limit.Headers.RetryAfter.Should().BeNull();
        (await Sink(id)).Should().HaveCount(1, "rejected resends never send a code");
    }

    [Fact]
    public async Task Confirming_a_channel_that_was_not_required_or_sent_is_refused()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com", phone: "+5511999990000", order: 1, confirmation: "[\"EMAIL\"]", type: "SIMPLE") + ","
            + Signer("Bia", email: "bia@example.com", order: 2, confirmation: "[\"EMAIL\"]", type: "SIMPLE"));
        await WaitCodes(id, 1);
        var d = await Detail(id);
        var a = SignerOf(d, 0).GetProperty("id").GetString()!;
        var b = SignerOf(d, 1).GetProperty("id").GetString()!;
        (await Confirm(id, a, "SMS", "123456")).StatusCode.Should().Be(HttpStatusCode.NotFound); // not required
        (await Confirm(id, b, "EMAIL", "123456")).StatusCode.Should().Be(HttpStatusCode.Conflict); // not their turn: no code sent
        (await Resend(id, b, "EMAIL")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Confirm(id, a, "EMAIL", "abc")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Confirm(id, a, "PIGEON", "123456")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- sequential order --------------------------------------------------------------------------

    [Fact]
    public async Task Sequential_signers_get_codes_and_sign_only_on_their_turn()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com", order: 1) + "," + Signer("Bia", email: "bia@example.com", order: 2),
            defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var first = await WaitCodes(id, 1);
        var a = await SignerId(id, 0);
        var b = await SignerId(id, 1);
        first.Single().SignerId.Should().Be(a);

        await Task.Delay(2500);
        (await Sink(id)).Should().HaveCount(1, "the second signer is not released while the first has not signed");

        (await Confirm(id, a, "EMAIL", first[0].Code)).StatusCode.Should().Be(HttpStatusCode.OK);
        var all = await WaitCodes(id, 2);
        var d = await Detail(id);
        SignerOf(d, 0).GetProperty("signed").GetBoolean().Should().BeTrue("the second code is only issued after the first signature");
        SignerOf(d, 1).GetProperty("signed").GetBoolean().Should().BeFalse();

        (await Confirm(id, b, "EMAIL", all.Single(c => c.SignerId == b).Code)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
        var done = await Detail(id);
        SignerOf(done, 0).GetProperty("signedAt").GetDateTime().Should().BeBefore(SignerOf(done, 1).GetProperty("signedAt").GetDateTime());
        var types = await EventTypes(id);
        types.Where(t => t == "SIGNER_SIGNED").Should().HaveCount(2);
    }

    [Fact]
    public async Task Order_without_confirmation_signs_in_sequence_and_equal_orders_sign_together()
    {
        var seq = await Create(Signer("Ana", order: 1) + "," + Signer("Bia", order: 2) + "," + Signer("Cris", order: 2), defaults: "{\"signatureType\":\"SIMPLE\"}");
        await f.WaitForStatusAsync(seq, "COMPLETED");
        var d = await Detail(seq);
        var at = Enumerable.Range(0, 3).Select(i => SignerOf(d, i).GetProperty("signedAt").GetDateTime()).ToList();
        at[0].Should().BeBefore(at[1]);
        at[0].Should().BeBefore(at[2]);
        (at[2] - at[1]).Duration().Should().BeLessThan(TimeSpan.FromSeconds(2.5), "signers of the same order are released together");
    }

    // ---- notifier resilience -----------------------------------------------------------------------

    [Fact]
    public async Task Transient_notifier_failures_are_retried_and_the_process_completes()
    {
        var id = await Create(Signer("Ana", email: "sim-flaky-2@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var code = (await WaitCodes(id, 1, 30))[0];
        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        ops.Single(o => o.GetProperty("type").GetString() == "CONFIRMATION_SEND").GetProperty("attempt").GetInt32().Should().BeGreaterThan(1);
        (await EventTypes(id)).Should().Contain("OPERATION_RETRY_SCHEDULED");
        (await Confirm(id, await SignerId(id, 0), "EMAIL", code.Code)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
    }

    [Fact]
    public async Task Exhausted_notifier_retries_go_to_the_notification_dead_letter_queue()
    {
        var id = await Create(Signer("Ana", email: "sim-down@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        var deadline = DateTime.UtcNow.AddSeconds(40);
        JsonElement dl = default;
        while (DateTime.UtcNow < deadline)
        {
            dl = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=notification&pageSize=200");
            if (dl.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("processId").GetString() == id)) break;
            await Task.Delay(500);
        }
        dl.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("processId").GetString() == id && i.GetProperty("operationType").GetString() == "CONFIRMATION_SEND");
        (await Sink(id)).Should().BeEmpty();
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
    }

    // ---- progress ----------------------------------------------------------------------------------

    [Fact]
    public async Task Progress_is_listed_and_cancelled_steps_do_not_count()
    {
        var id = await Create(Signer("Ana", email: "ana@example.com"), defaults: "{\"signatureType\":\"SIMPLE\",\"confirmation\":[\"EMAIL\"]}");
        await WaitCodes(id, 1);
        var inFlight = (await f.Client.GetFromJsonAsync<JsonElement>("/v1/signature-processes?pageSize=200")).GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("processId").GetString() == id).GetProperty("progress");
        (inFlight.GetProperty("completedSteps").GetInt32(), inFlight.GetProperty("totalSteps").GetInt32()).Should().Be((1, 4));
        inFlight.GetProperty("currentStep").GetProperty("kind").GetString().Should().Be("CONFIRMATION");
        inFlight.TryGetProperty("steps", out _).Should().BeFalse("the list carries the summary only");

        (await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = (await Detail(id)).GetProperty("progress");
        cancelled.GetProperty("completedSteps").GetInt32().Should().Be(1);
        cancelled.GetProperty("totalSteps").GetInt32().Should().Be(4);
        cancelled.GetProperty("currentStep").ValueKind.Should().Be(JsonValueKind.Null);
        cancelled.GetProperty("steps").EnumerateArray().Skip(1).Should().OnlyContain(s => s.GetProperty("status").GetString() == "CANCELLED");
        (await Confirm(id, await SignerId(id, 0), "EMAIL", "123456")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Progress_reaches_the_total_only_after_the_final_callback_is_delivered()
    {
        var key = "prog-" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync(callbackJson: "{\"url\":\"" + f.CallbackUrl(key) + "\"}");
        await f.WaitForStatusAsync(id, "COMPLETED");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement p;
        do { p = (await Detail(id)).GetProperty("progress"); if (p.GetProperty("completedSteps").GetInt32() == p.GetProperty("totalSteps").GetInt32()) break; await Task.Delay(300); }
        while (DateTime.UtcNow < deadline);
        p.GetProperty("totalSteps").GetInt32().Should().Be(4);
        p.GetProperty("completedSteps").GetInt32().Should().Be(4);
        p.GetProperty("steps").EnumerateArray().Last().GetProperty("kind").GetString().Should().Be("CALLBACK");
    }

    [Fact]
    public async Task Failed_provider_step_does_not_count_as_completed()
    {
        var id = await f.CreateProcessAsync("SIM-FAIL-" + Guid.NewGuid().ToString("N")[..6]);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement p;
        do { await Task.Delay(300); p = (await Detail(id)).GetProperty("progress"); }
        while ((await Detail(id)).GetProperty("operationalStatus").GetString() != "MANUAL_ACTION" && DateTime.UtcNow < deadline);
        p.GetProperty("completedSteps").GetInt32().Should().Be(1, "only the document was received");
        p.GetProperty("totalSteps").GetInt32().Should().Be(3);
    }

    // ---- upload ------------------------------------------------------------------------------------

    private async Task<JsonElement> Upload(HttpClient client, byte[]? content = null, string fileName = "contract.pdf")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content ?? TestFixture.DocBytes);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", fileName);
        var r = await client.PostAsync("/v1/document-uploads", form);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Uploaded_document_is_the_source_of_a_process()
    {
        var up = await Upload(f.Client);
        up.GetProperty("size").GetInt64().Should().Be(TestFixture.DocBytes.Length);
        var id = await Create(Signer("Ana", type: "SIMPLE"), source: "{\"type\":\"UPLOAD\",\"uploadId\":\"" + up.GetProperty("uploadId").GetString() + "\"}");
        await f.WaitForStatusAsync(id, "COMPLETED");
        var items = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").EnumerateArray().ToList();
        items.Single(a => a.GetProperty("type").GetString() == "ORIGINAL_DOCUMENT").GetProperty("sha256").GetString().Should().Be(up.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Unknown_upload_and_bad_upload_requests_are_refused()
    {
        var r = await f.CreateAsync(Guid.NewGuid().ToString(), Body(Signer("Ana", type: "SIMPLE"), source: "{\"type\":\"UPLOAD\",\"uploadId\":\"upl_nope\"}"));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("document.source.uploadId");

        (await f.Client.PostAsync("/v1/document-uploads", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var empty = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "x.pdf" } };
        (await f.Client.PostAsync("/v1/document-uploads", empty)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var big = new MultipartFormDataContent { { new ByteArrayContent(new byte[2 * 1024 * 1024]), "file", "big.pdf" } }; // limit is 1 MiB in tests
        (await f.Client.PostAsync("/v1/document-uploads", big)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Uploads_belong_to_the_client_that_sent_them()
    {
        using var api = f.CreateAuthClient();
        var a = Tokens.Make("client-a", ["client"]);
        var b = Tokens.Make("client-b", ["client"]);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestFixture.DocBytes);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", "a.pdf");
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/document-uploads") { Content = form };
        req.Headers.Authorization = new("Bearer", a);
        var up = await (await api.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var source = "{\"type\":\"UPLOAD\",\"uploadId\":\"" + up.GetProperty("uploadId").GetString() + "\"}";

        var asB = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", b, Body(Signer("Ana", type: "SIMPLE"), source: source), Guid.NewGuid().ToString()));
        asB.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var asA = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", a, Body(Signer("Ana", type: "SIMPLE"), source: source), Guid.NewGuid().ToString()));
        asA.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var operatorToken = Tokens.Make("olga", ["operator"]);
        var asOperator = new HttpRequestMessage(HttpMethod.Post, "/v1/document-uploads") { Content = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "x.pdf" } } };
        asOperator.Headers.Authorization = new("Bearer", operatorToken);
        (await api.SendAsync(asOperator)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_development_sink_is_admin_only_and_off_by_default()
    {
        using var api = f.CreateAuthClient();
        var client = Tokens.Make("client-a", ["client"]);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/dev/confirmation-codes/sig_1", client))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/dev/confirmation-codes/sig_1", Tokens.Make("root", ["admin"])))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var off = f.CreateApiClient(new() { ["Confirmation:ExposeSink"] = "false" });
        (await off.GetAsync("/v1/dev/confirmation-codes/sig_1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

[Collection("integration")]
public class SwaggerExamplesTests(TestFixture f)
{
    [Fact]
    public async Task Swagger_documents_the_new_fields_and_endpoints_with_examples()
    {
        var doc = await f.Client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = doc.GetProperty("paths");

        var create = paths.GetProperty("/v1/signature-processes").GetProperty("post");
        var examples = create.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("examples");
        examples.EnumerateObject().Select(e => e.Name).Should().BeEquivalentTo("simples", "sequencial", "legado");
        var simple = examples.GetProperty("simples").GetProperty("value");
        simple.GetProperty("defaults").GetProperty("confirmation").GetArrayLength().Should().Be(2);
        simple.GetProperty("signers")[0].GetProperty("phone").GetString().Should().StartWith("+55");
        examples.GetProperty("sequencial").GetProperty("value").GetProperty("signers")[1].GetProperty("order").GetInt32().Should().Be(2);
        create.GetProperty("responses").GetProperty("400").GetProperty("content").GetProperty("application/json").GetProperty("example")
            .GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Contain("signers[1].phone");

        paths.GetProperty("/v1/signature-processes").GetProperty("get").GetProperty("responses").GetProperty("200").ToString().Should().Contain("completedSteps");
        paths.GetProperty("/v1/signature-processes/{id}").GetProperty("get").GetProperty("responses").GetProperty("200").ToString().Should().Contain("steps");

        var confirm = paths.GetProperty("/v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/confirm").GetProperty("post");
        confirm.GetProperty("responses").EnumerateObject().Select(r => r.Name).Should().Contain(["200", "410", "422", "423"]);
        paths.GetProperty("/v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/resend").GetProperty("post")
            .GetProperty("responses").EnumerateObject().Select(r => r.Name).Should().Contain(["202", "429"]);
        paths.GetProperty("/v1/document-uploads").GetProperty("post").GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("multipart/form-data", out _).Should().BeTrue();
        paths.TryGetProperty("/v1/dev/confirmation-codes/{processId}", out _).Should().BeFalse("the development sink is not part of the public contract");
    }
}
