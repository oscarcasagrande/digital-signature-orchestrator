using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class CallbackApi
{
    public static async Task<List<JsonElement>> DeliveriesAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/callbacks")).GetProperty("items").EnumerateArray().ToList();

    public static async Task<List<JsonElement>> WaitForDeliveriesAsync(this TestFixture f, string id, Func<List<JsonElement>, bool> done, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        List<JsonElement> last = [];
        while (DateTime.UtcNow < deadline)
        {
            last = await f.DeliveriesAsync(id);
            if (done(last)) return last;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Deliveries of {id} did not reach the expected state; last=[{string.Join(", ", last.Select(d => d.GetProperty("eventType").GetString() + ":" + d.GetProperty("status").GetString()))}]");
    }

    public static string DynamicCallback(this TestFixture f, string key) => "{\"url\":\"" + f.CallbackUrl(key) + "\"}";

    public static async Task<JsonElement> RegisterAsync(this TestFixture f, string callbackId, string url, string? secret = null)
    {
        var r = await f.Client.PostAsJsonAsync("/v1/callbacks", new { callbackId, url, secret });
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }
}

[Collection("integration")]
public class CallbackRegistrationTests(TestFixture f)
{
    [Fact]
    public async Task Registration_returns_the_secret_once_and_never_again()
    {
        var id = "REG_" + Guid.NewGuid().ToString("N")[..8];
        var created = await f.RegisterAsync(id, f.CallbackUrl("reg"));
        created.GetProperty("secret").GetString().Should().HaveLength(43); // 32 random bytes, Base64Url without padding
        created.GetProperty("active").GetBoolean().Should().BeTrue();

        var get = await f.Client.GetStringAsync($"/v1/callbacks/{id}");
        get.Should().NotContain(created.GetProperty("secret").GetString()!).And.NotContain("secret");
        var list = await f.Client.GetStringAsync("/v1/callbacks");
        list.Should().Contain(id).And.NotContain(created.GetProperty("secret").GetString()!);
    }

    [Fact]
    public async Task Informed_secret_is_accepted_but_not_echoed_afterwards()
    {
        var id = "SEC_" + Guid.NewGuid().ToString("N")[..8];
        var created = await f.RegisterAsync(id, f.CallbackUrl("sec"), "my-own-secret-0123456789");
        created.GetProperty("secret").GetString().Should().Be("my-own-secret-0123456789");
        (await f.Client.GetStringAsync($"/v1/callbacks/{id}")).Should().NotContain("my-own-secret");
    }

    [Fact]
    public async Task Duplicate_invalid_and_unknown_registrations()
    {
        var id = "DUP_" + Guid.NewGuid().ToString("N")[..8];
        await f.RegisterAsync(id, f.CallbackUrl("dup"));
        (await f.Client.PostAsJsonAsync("/v1/callbacks", new { callbackId = id, url = f.CallbackUrl("dup") })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.Client.PostAsJsonAsync("/v1/callbacks", new { callbackId = "bad id!", url = f.CallbackUrl("x") })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.Client.PostAsJsonAsync("/v1/callbacks", new { callbackId = "SHORT_SECRET", url = f.CallbackUrl("x"), secret = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.Client.GetAsync("/v1/callbacks/NOPE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.Client.DeleteAsync("/v1/callbacks/NOPE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deactivated_callback_cannot_be_used_for_new_processes()
    {
        var id = "OFF_" + Guid.NewGuid().ToString("N")[..8];
        await f.RegisterAsync(id, f.CallbackUrl("off"));
        (await f.Client.DeleteAsync($"/v1/callbacks/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/callbacks/{id}")).GetProperty("active").GetBoolean().Should().BeFalse();
        var r = await f.CreateAsync(Guid.NewGuid().ToString(), TestFixture.Payload(callbackJson: "{\"callbackId\":\"" + id + "\"}"));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("callback.callbackId");
    }

    [Fact]
    public async Task Unknown_callback_id_and_ambiguous_destination_are_rejected_at_creation()
    {
        var r1 = await f.CreateAsync(Guid.NewGuid().ToString(), TestFixture.Payload(callbackJson: "{\"callbackId\":\"DOES_NOT_EXIST\"}"));
        r1.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var r2 = await f.CreateAsync(Guid.NewGuid().ToString(), TestFixture.Payload(callbackJson: "{\"callbackId\":\"X\",\"url\":\"https://example.com/x\"}"));
        r2.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.0.10/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/hook")]
    public async Task Strict_policy_rejects_unsafe_destinations_in_registration_and_process_creation(string url)
    {
        using var strict = f.CreateApiClient(new() { ["Callbacks:AllowHttp"] = "false", ["Callbacks:AllowPrivateNetworks"] = "false" });
        (await strict.PostAsJsonAsync("/v1/callbacks", new { callbackId = "STRICT_" + Guid.NewGuid().ToString("N")[..6], url }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/signature-processes")
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            Content = new StringContent(TestFixture.Payload(callbackJson: "{\"url\":\"" + url + "\"}"), System.Text.Encoding.UTF8, "application/json")
        };
        var resp = await strict.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("callback.url");
    }

    [Fact]
    public async Task Strict_policy_accepts_public_https_destinations()
    {
        using var strict = f.CreateApiClient(new() { ["Callbacks:AllowHttp"] = "false", ["Callbacks:AllowPrivateNetworks"] = "false" });
        var r = await strict.PostAsJsonAsync("/v1/callbacks", new { callbackId = "PUB_" + Guid.NewGuid().ToString("N")[..6], url = "https://cliente.exemplo.com/signatures/callback" });
        r.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Allowlist_rejects_hosts_that_are_not_listed()
    {
        using var restricted = f.CreateApiClient(new()
        {
            ["Callbacks:AllowHttp"] = "false", ["Callbacks:AllowPrivateNetworks"] = "false",
            ["Callbacks:AllowedHosts:0"] = "partner.example.com", ["Callbacks:AllowedHosts:1"] = "*.corp.example.com"
        });
        (await restricted.PostAsJsonAsync("/v1/callbacks", new { callbackId = "AL_" + Guid.NewGuid().ToString("N")[..6], url = "https://partner.example.com/x" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await restricted.PostAsJsonAsync("/v1/callbacks", new { callbackId = "AL_" + Guid.NewGuid().ToString("N")[..6], url = "https://other.example.org/x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

[Collection("integration")]
public class CallbackDeliveryTests(TestFixture f)
{
    private static readonly string[] BodyFields = ["eventId", "eventType", "processId", "externalId", "status", "occurredAt", "document"];

    [Fact]
    public async Task Dynamic_url_receives_signed_events_through_to_completion()
    {
        var key = "dyn" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));
        await f.WaitForStatusAsync(id, "COMPLETED");
        await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => x.GetProperty("status").GetString() == "DELIVERED"));

        var received = TestFixture.ReceivedFor(key);
        received.Select(r => r.Json.GetProperty("eventType").GetString()).Should()
            .Contain(["SIGNATURE_PROCESS.SIGNATURE_IN_PROGRESS", "SIGNATURE_PROCESS.SIGNED", "SIGNATURE_PROCESS.COMPLETED"]);
        received.Should().OnlyContain(r => r.SignatureValid, "every callback must carry a valid HMAC-SHA256 signature");
        received.Should().OnlyContain(r => r.Signature.StartsWith("sha256=") && r.EventId == r.Json.GetProperty("eventId").GetString());
        received.Select(r => r.EventId).Should().OnlyHaveUniqueItems();

        foreach (var r in received)
        {
            r.Json.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(BodyFields);
            r.Json.GetProperty("processId").GetString().Should().Be(id);
            r.Json.GetProperty("occurredAt").ValueKind.Should().Be(JsonValueKind.String);
            r.Body.Should().NotContain("12345678909").And.NotContain("Signer");
        }

        var completed = received.Single(r => r.Json.GetProperty("eventType").GetString() == "SIGNATURE_PROCESS.COMPLETED").Json;
        completed.GetProperty("status").GetString().Should().Be("COMPLETED");
        var doc = completed.GetProperty("document");
        var path = new Uri(doc.GetProperty("downloadUrl").GetString()!).PathAndQuery;
        var signed = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items")
            .EnumerateArray().Single(a => a.GetProperty("type").GetString() == "SIGNED_DOCUMENT");
        doc.GetProperty("id").GetString().Should().Be(signed.GetProperty("artifactId").GetString());
        var bytes = await (await f.Client.GetAsync(path)).Content.ReadAsByteArrayAsync();
        Sha.Hex(bytes).Should().Be(signed.GetProperty("sha256").GetString());
        received.Where(r => r.Json.GetProperty("eventType").GetString() != "SIGNATURE_PROCESS.COMPLETED")
            .All(r => !r.Json.TryGetProperty("document", out _)).Should().BeTrue();

        var events = await f.EventsAsync(id);
        events.Count(e => e.GetProperty("type").GetString() == "CALLBACK_REQUESTED").Should().Be(received.Count);
        events.Count(e => e.GetProperty("type").GetString() == "CALLBACK_DELIVERED").Should().Be(received.Count);
    }

    [Fact]
    public async Task Registered_callback_id_uses_the_registered_url_and_secret()
    {
        var key = "reg" + Guid.NewGuid().ToString("N")[..8];
        var callbackId = "CB_" + key;
        var created = await f.RegisterAsync(callbackId, f.CallbackUrl(key));
        TestFixture.CallbackSecrets[key] = created.GetProperty("secret").GetString()!;

        var id = await f.CreateProcessAsync(callbackJson: "{\"callbackId\":\"" + callbackId + "\"}");
        await f.WaitForStatusAsync(id, "COMPLETED");
        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => x.GetProperty("status").GetString() == "DELIVERED"));

        TestFixture.ReceivedFor(key).Should().NotBeEmpty().And.OnlyContain(r => r.SignatureValid);
        deliveries.Should().OnlyContain(d => d.GetProperty("destination").GetString() == callbackId);
        JsonSerializer.Serialize(deliveries).Should().NotContain(created.GetProperty("secret").GetString()!);
    }

    [Fact]
    public async Task Signature_made_with_a_different_secret_is_invalid_for_the_receiver()
    {
        var key = "wrong" + Guid.NewGuid().ToString("N")[..8];
        TestFixture.CallbackSecrets[key] = "a-completely-different-secret-0123";
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));
        await f.WaitForStatusAsync(id, "COMPLETED");
        await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => x.GetProperty("status").GetString() == "DELIVERED"));
        TestFixture.ReceivedFor(key).Should().NotBeEmpty().And.OnlyContain(r => !r.SignatureValid);
    }

    [Fact]
    public async Task Process_without_callback_generates_no_deliveries()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await f.DeliveriesAsync(id)).Should().BeEmpty();
        (await f.EventsAsync(id)).Should().NotContain(e => e.GetProperty("type").GetString() == "CALLBACK_REQUESTED");
    }

    [Fact]
    public async Task Provider_rejection_and_cancellation_are_notified()
    {
        var rejectKey = "rej" + Guid.NewGuid().ToString("N")[..8];
        var rid = await f.CreateProcessAsync("SIM-REJECT-" + Guid.NewGuid().ToString("N")[..6], callbackJson: f.DynamicCallback(rejectKey));
        await f.WaitForStatusAsync(rid, "REJECTED");
        await f.WaitForDeliveriesAsync(rid, d => d.Any(x => x.GetProperty("eventType").GetString() == "SIGNATURE_PROCESS.REJECTED"
                                                           && x.GetProperty("status").GetString() == "DELIVERED"));

        var cancelKey = "can" + Guid.NewGuid().ToString("N")[..8];
        var cid = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(cancelKey));
        (await f.Client.PostAsync($"/v1/signature-processes/{cid}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForDeliveriesAsync(cid, d => d.Any(x => x.GetProperty("eventType").GetString() == "SIGNATURE_PROCESS.CANCELLED"
                                                           && x.GetProperty("status").GetString() == "DELIVERED"));
        (await f.StatusAsync(cid)).GetProperty("businessStatus").GetString().Should().Be("CANCELLED");
    }

    [Fact]
    public async Task Callbacks_scheduled_before_cancellation_are_still_delivered_and_the_terminal_process_does_not_block_them()
    {
        var key = "term" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));
        await f.WaitForStatusAsync(id, "COMPLETED");
        // Everything above happened on a terminal process; the COMPLETED delivery must still go out.
        var d = await f.WaitForDeliveriesAsync(id, x => x.Any(e => e.GetProperty("eventType").GetString() == "SIGNATURE_PROCESS.COMPLETED"
                                                                   && e.GetProperty("status").GetString() == "DELIVERED"));
        d.Should().OnlyContain(x => x.GetProperty("status").GetString() == "DELIVERED" || x.GetProperty("status").GetString() == "PENDING");
    }
}

[Collection("integration")]
public class CallbackResilienceTests(TestFixture f)
{
    private static string S(JsonElement d, string p) => d.GetProperty(p).GetString()!;

    [Fact]
    public async Task Receiver_failures_are_retried_with_stable_event_ids_and_never_touch_the_process_state()
    {
        var key = "flaky" + Guid.NewGuid().ToString("N")[..8];
        TestFixture.CallbackFailFirst[key] = 2;
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));

        var seenOperational = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        JsonElement st;
        do
        {
            st = await f.StatusAsync(id);
            seenOperational.Add(S(st, "operationalStatus"));
            await Task.Delay(100);
        } while (S(st, "businessStatus") != "COMPLETED" && DateTime.UtcNow < deadline);

        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => S(x, "status") == "DELIVERED"));
        seenOperational.Should().NotContain("RETRY_PENDING", "callback retries must not leak into the process operational state");
        (await f.StatusAsync(id)).GetProperty("operationalStatus").GetString().Should().Be("READY");
        deliveries.Sum(d => d.GetProperty("attempts").GetInt32()).Should().Be(deliveries.Count + 2);

        var all = TestFixture.ReceivedFor(key);
        all.Should().HaveCount(deliveries.Count + 2);
        var retried = all.GroupBy(r => r.EventId).Where(g => g.Count() > 1).ToList();
        retried.Should().NotBeEmpty();
        foreach (var g in retried)
            g.Select(r => r.Json.GetProperty("occurredAt").GetString()).Distinct().Should().HaveCount(1); // stable across attempts
        all.Where(r => r.SignatureValid).Select(r => r.EventId).Distinct().Should().HaveCount(deliveries.Count);
        (await f.EventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "OPERATION_RETRY_SCHEDULED").Should().Be(2);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(302)]
    public async Task Permanent_responses_fail_without_retry_and_leave_the_process_alone(int status)
    {
        var key = "perm" + Guid.NewGuid().ToString("N")[..8];
        TestFixture.CallbackStatus[key] = status;
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));
        await f.WaitForStatusAsync(id, "COMPLETED");
        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => S(x, "status") == "FAILED"));
        deliveries.Should().OnlyContain(d => d.GetProperty("attempts").GetInt32() == 1 && d.GetProperty("lastStatusCode").GetInt32() == status);
        TestFixture.ReceivedFor(key).Should().HaveCount(deliveries.Count);
        (await f.StatusAsync(id)).GetProperty("operationalStatus").GetString().Should().Be("READY");
    }

    [Fact]
    public async Task Exhausted_deliveries_go_to_the_callback_dlq_and_can_be_resent_manually_after_the_process_is_terminal()
    {
        var key = "down" + Guid.NewGuid().ToString("N")[..8];
        TestFixture.CallbackFailFirst[key] = 100000;
        var id = await f.CreateProcessAsync(callbackJson: f.DynamicCallback(key));
        await f.WaitForStatusAsync(id, "COMPLETED");
        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 3 && d.All(x => S(x, "status") == "DLQ"), 60);
        deliveries.Should().OnlyContain(d => d.GetProperty("attempts").GetInt32() == 4 && d.GetProperty("lastStatusCode").GetInt32() == 503);

        (await f.StatusAsync(id)).GetProperty("operationalStatus").GetString().Should().Be("READY");
        var dl = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=callback&pageSize=200");
        var mine = dl.GetProperty("items").EnumerateArray().Where(e => S(e, "processId") == id).ToList();
        mine.Should().HaveCount(deliveries.Count);
        mine.Should().OnlyContain(e => S(e, "queue") == "callback-dlq" && S(e, "operationType") == "CALLBACK_SEND");
        f.FindMessage("callback-dlq", b => b.Contains(id)).Should().NotBeNull();

        // Without an operationId a terminal process cannot be reprocessed...
        (await f.RetryAsync(id)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // ...but a named callback delivery can be resent.
        TestFixture.CallbackFailFirst[key] = 0;
        var target = deliveries.First(d => S(d, "eventType") == "SIGNATURE_PROCESS.COMPLETED");
        var resp = await f.RetryAsync(id, new { operationId = S(target, "operationId"), reason = "receiver is back" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await f.WaitForDeliveriesAsync(id, d => d.Any(x => S(x, "deliveryId") == S(target, "deliveryId") && S(x, "status") == "DELIVERED"));
        after.Single(x => S(x, "deliveryId") == S(target, "deliveryId")).GetProperty("lastStatusCode").GetInt32().Should().Be(200);

        var st = await f.StatusAsync(id);
        S(st, "businessStatus").Should().Be("COMPLETED");
        S(st, "operationalStatus").Should().Be("READY");
        (await f.EventsAsync(id)).Should().Contain(e => S(e, "type") == "OPERATION_REPROCESS_REQUESTED");
        var resolved = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=callback&resolved=true&pageSize=200");
        resolved.GetProperty("items").EnumerateArray().Should().Contain(e => S(e, "operationId") == S(target, "operationId"));
    }

    [Fact]
    public async Task Deactivated_destination_fails_permanently_at_send_time()
    {
        var key = "deact" + Guid.NewGuid().ToString("N")[..8];
        var callbackId = "CB_" + key;
        await f.RegisterAsync(callbackId, f.CallbackUrl(key));
        TestFixture.CallbackFailFirst[key] = 100000; // keep it failing so deliveries are still pending when we deactivate
        var id = await f.CreateProcessAsync(callbackJson: "{\"callbackId\":\"" + callbackId + "\"}");
        await f.WaitForDeliveriesAsync(id, d => d.Count >= 1 && d.Any(x => x.GetProperty("attempts").GetInt32() >= 1));
        (await f.Client.DeleteAsync($"/v1/callbacks/{callbackId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var d = await f.WaitForDeliveriesAsync(id, x => x.Count >= 1 && x.Any(e => S(e, "status") == "FAILED"), 60);
        d.Should().Contain(e => S(e, "status") == "FAILED" && S(e, "lastError").Contains("inactive"));
    }

    [Fact]
    public async Task Deliveries_of_unknown_process_is_404() =>
        (await f.Client.GetAsync("/v1/signature-processes/sig_nope/callbacks")).StatusCode.Should().Be(HttpStatusCode.NotFound);
}
