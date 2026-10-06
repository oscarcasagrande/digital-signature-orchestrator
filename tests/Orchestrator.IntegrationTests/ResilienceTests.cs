using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Infrastructure.Persistence;
using RabbitMQ.Client;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class Api
{
    public static async Task<JsonElement> StatusAsync(this TestFixture f, string id) =>
        await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");

    public static async Task<List<JsonElement>> OpsAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200"))
        .GetProperty("items").EnumerateArray().ToList();

    public static async Task<List<JsonElement>> EventsAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=200"))
        .GetProperty("items").EnumerateArray().ToList();

    public static async Task<JsonElement> WaitForOperationalAsync(this TestFixture f, string id, string operational, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonElement st = default;
        while (DateTime.UtcNow < deadline)
        {
            st = await f.StatusAsync(id);
            if (st.GetProperty("operationalStatus").GetString() == operational) return st;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Process {id} did not reach operational status {operational}; last={st}");
    }

    public static async Task<JsonElement> WaitForOperationAsync(this TestFixture f, string id, string type, string status, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var op = (await f.OpsAsync(id)).LastOrDefault(o => o.GetProperty("type").GetString() == type);
            if (op.ValueKind == JsonValueKind.Object && op.GetProperty("status").GetString() == status) return op;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Operation {type} of {id} did not reach {status}");
    }

    public static Task<HttpResponseMessage> RetryAsync(this TestFixture f, string id, object? body = null) =>
        f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/retry", body ?? new { });

    /// <summary>Reads messages from a broker queue until the predicate matches (or times out).</summary>
    public static string? FindMessage(this TestFixture f, string queue, Func<string, bool> match, int timeoutSeconds = 20)
    {
        var factory = new ConnectionFactory { HostName = f.RabbitHost, Port = f.RabbitPort, UserName = "rabbitmq", Password = "rabbitmq" };
        using var conn = factory.CreateConnection();
        using var ch = conn.CreateModel();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = ch.BasicGet(queue, autoAck: true);
            if (r is null) { Thread.Sleep(200); continue; }
            var body = Encoding.UTF8.GetString(r.Body.Span);
            if (match(body)) return body;
        }
        return null;
    }
}

[Collection("integration")]
public class RetryTests(TestFixture f)
{
    [Fact]
    public async Task Transient_provider_failures_are_retried_until_the_process_completes()
    {
        var id = await f.CreateProcessAsync("SIM-FLAKY-2-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForStatusAsync(id, "COMPLETED");

        var create = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "PROVIDER_CREATE_PROCESS");
        create.GetProperty("attempt").GetInt32().Should().Be(3);
        create.GetProperty("status").GetString().Should().Be("COMPLETED");

        var events = await f.EventsAsync(id);
        var retries = events.Where(e => e.GetProperty("type").GetString() == "OPERATION_RETRY_SCHEDULED").ToList();
        retries.Should().HaveCount(2);
        foreach (var r in retries)
        {
            var meta = r.GetProperty("metadata");
            meta.GetProperty("errorClass").GetString().Should().Be("TRANSIENT");
            meta.GetProperty("nextRetryAt").GetDateTime().Should().BeAfter(r.GetProperty("timestamp").GetDateTime());
        }
        (await f.StatusAsync(id)).GetProperty("operationalStatus").GetString().Should().Be("READY");
    }

    [Fact]
    public async Task Source_503_is_transient_and_recovers_when_the_source_recovers()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync(documentUrl: $"{f.OriginUrl}/flaky/2/{key}");
        await f.WaitForStatusAsync(id, "COMPLETED");
        var download = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD");
        download.GetProperty("attempt").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Retry_pending_state_exposes_attempt_max_attempts_and_next_retry_time_and_cancel_stops_it()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 503;
        try
        {
            var id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            var st = await f.WaitForOperationalAsync(id, "RETRY_PENDING");
            st.GetProperty("businessStatus").GetString().Should().Be("CREATED");

            var op = await f.WaitForOperationAsync(id, "DOCUMENT_DOWNLOAD", "RETRY_PENDING");
            op.GetProperty("attempt").GetInt32().Should().BeGreaterThanOrEqualTo(1);
            op.GetProperty("maxAttempts").GetInt32().Should().Be(4);
            op.GetProperty("nextRetryAt").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddSeconds(-1));
            op.GetProperty("error").GetProperty("errorClass").GetString().Should().Be("TRANSIENT");

            var cancel = await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null);
            cancel.StatusCode.Should().Be(HttpStatusCode.OK);
            var attemptAtCancel = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD");
            attemptAtCancel.GetProperty("status").GetString().Should().Be("CANCELLED");

            TestFixture.ToggleDown.TryRemove(key, out _); // source recovers, but the process is cancelled
            await Task.Delay(4000);
            (await f.StatusAsync(id)).GetProperty("businessStatus").GetString().Should().Be("CANCELLED");
            var after = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD");
            after.GetProperty("attempt").GetInt32().Should().Be(attemptAtCancel.GetProperty("attempt").GetInt32());
            (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").GetArrayLength().Should().Be(0);
        }
        finally { TestFixture.ToggleDown.TryRemove(key, out _); }
    }
}

[Collection("integration")]
public class DeadLetterTests(TestFixture f)
{
    private async Task<JsonElement> DeadLettersAsync(string query) =>
        await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters" + query);

    [Fact]
    public async Task Exhausted_provider_operation_goes_to_the_provider_dlq_with_references_only()
    {
        var ext = "SIM-DOWN-" + Guid.NewGuid().ToString("N")[..6];
        var id = await f.CreateProcessAsync(ext);
        await f.WaitForOperationalAsync(id, "DLQ");

        var op = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "PROVIDER_CREATE_PROCESS");
        op.GetProperty("status").GetString().Should().Be("DLQ");
        op.GetProperty("attempt").GetInt32().Should().Be(4);

        var list = await DeadLettersAsync("?domain=signature-provider");
        var entry = list.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("processId").GetString() == id);
        entry.GetProperty("operationId").GetString().Should().Be(op.GetProperty("operationId").GetString());
        entry.GetProperty("domain").GetString().Should().Be("signature-provider");
        entry.GetProperty("queue").GetString().Should().Be("signature-provider-dlq");
        entry.GetProperty("errorClass").GetString().Should().Be("TRANSIENT");
        entry.GetProperty("attempts").GetInt32().Should().Be(4);
        entry.GetProperty("resolvedAt").ValueKind.Should().Be(JsonValueKind.Null);

        (await DeadLettersAsync("?domain=artifact")).GetProperty("items").EnumerateArray()
            .Should().NotContain(e => e.GetProperty("processId").GetString() == id);

        var events = await f.EventsAsync(id);
        events.Count(e => e.GetProperty("type").GetString() == "OPERATION_RETRY_SCHEDULED").Should().Be(3);
        events.Should().Contain(e => e.GetProperty("type").GetString() == "OPERATION_DEAD_LETTERED");

        var msg = f.FindMessage("signature-provider-dlq", b => b.Contains(id));
        msg.Should().NotBeNull();
        var names = JsonDocument.Parse(msg!).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().BeSubsetOf(["processId", "operationId", "deadLetterId", "domain", "errorClass", "correlationId", "causationId"]);
        msg.Should().NotContain(ext).And.NotContain("12345678909").And.NotContain("Joao");
    }

    [Fact]
    public async Task Exhausted_document_operation_goes_to_the_artifact_dlq()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 503;
        try
        {
            var id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(id, "DLQ", 60);
            var list = await DeadLettersAsync("?domain=artifact");
            var entry = list.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("processId").GetString() == id);
            entry.GetProperty("operationType").GetString().Should().Be("DOCUMENT_DOWNLOAD");
            entry.GetProperty("queue").GetString().Should().Be("artifact-dlq");
            f.FindMessage("artifact-dlq", b => b.Contains(id)).Should().NotBeNull();
        }
        finally { TestFixture.ToggleDown.TryRemove(key, out _); }
    }

    [Fact]
    public async Task Unknown_errors_get_limited_retries_then_dlq()
    {
        var id = await f.CreateProcessAsync("SIM-UNKNOWN-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForOperationalAsync(id, "DLQ");
        var op = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "PROVIDER_CREATE_PROCESS");
        op.GetProperty("attempt").GetInt32().Should().Be(2); // Retry:UnknownMaxAttempts = 2 in the test setup
        op.GetProperty("error").GetProperty("errorClass").GetString().Should().Be("UNKNOWN");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forbidden")]
    public async Task Permanent_errors_are_not_retried(string path)
    {
        var id = await f.CreateProcessAsync(documentUrl: $"{f.OriginUrl}/{path}");
        await f.WaitForOperationalAsync(id, "MANUAL_ACTION");
        var op = (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD");
        op.GetProperty("status").GetString().Should().Be("FAILED");
        op.GetProperty("attempt").GetInt32().Should().Be(1);
        op.GetProperty("error").GetProperty("errorClass").GetString().Should().Be("PERMANENT");
        (await f.EventsAsync(id)).Should().NotContain(e => e.GetProperty("type").GetString() == "OPERATION_RETRY_SCHEDULED");
    }

    [Fact]
    public async Task Malformed_message_is_rejected_to_the_domain_dlq_and_the_consumer_survives()
    {
        var garbage = "not-json-" + Guid.NewGuid().ToString("N");
        var factory = new ConnectionFactory { HostName = f.RabbitHost, Port = f.RabbitPort, UserName = "rabbitmq", Password = "rabbitmq" };
        using (var conn = factory.CreateConnection())
        using (var ch = conn.CreateModel())
        {
            var props = ch.CreateBasicProperties();
            props.MessageId = "msg_malformed_" + Guid.NewGuid().ToString("N");
            ch.BasicPublish("orchestrator.commands", "signature-provider", props, Encoding.UTF8.GetBytes(garbage));
        }
        f.FindMessage("signature-provider-dlq", b => b == garbage).Should().Be(garbage);

        var id = await f.CreateProcessAsync(); // still processing normally
        await f.WaitForStatusAsync(id, "COMPLETED");
    }

    [Fact]
    public async Task Dead_letter_listing_validates_domain_and_paginates()
    {
        (await f.Client.GetAsync("/v1/dead-letters?domain=nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var page = await DeadLettersAsync("?page=1&pageSize=1");
        page.GetProperty("pageSize").GetInt32().Should().Be(1);
        page.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(1);
        page.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }
}

[Collection("integration")]
public class ReprocessTests(TestFixture f)
{
    [Fact]
    public async Task Retry_resumes_from_the_failed_operation_without_repeating_completed_ones()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 503;
        string id;
        try
        {
            id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(id, "DLQ", 60);
        }
        catch { TestFixture.ToggleDown.TryRemove(key, out _); throw; }

        var failed = (await f.OpsAsync(id)).Single(o => o.GetProperty("status").GetString() == "DLQ");
        TestFixture.ToggleDown.TryRemove(key, out _); // cause fixed

        var resp = await f.RetryAsync(id, new { reason = "source fixed" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("operationId").GetString().Should().Be(failed.GetProperty("operationId").GetString());
        body.GetProperty("operationType").GetString().Should().Be("DOCUMENT_DOWNLOAD");

        await f.WaitForStatusAsync(id, "COMPLETED");

        var ops = await f.OpsAsync(id);
        ops.Count(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD").Should().Be(1); // same operation, not a new process
        ops.Single(o => o.GetProperty("type").GetString() == "DOCUMENT_DOWNLOAD").GetProperty("status").GetString().Should().Be("COMPLETED");
        foreach (var t in new[] { "DOCUMENT_STORE", "PROVIDER_CREATE_PROCESS", "PROVIDER_SEND_DOCUMENT", "SIGNED_DOCUMENT_DOWNLOAD", "SIGNED_DOCUMENT_STORE" })
        {
            var o = ops.Single(x => x.GetProperty("type").GetString() == t);
            o.GetProperty("attempt").GetInt32().Should().Be(1);
        }
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").GetArrayLength().Should().Be(3);

        var events = await f.EventsAsync(id);
        var req = events.Single(e => e.GetProperty("type").GetString() == "OPERATION_REPROCESS_REQUESTED");
        req.GetProperty("metadata").GetProperty("reason").GetString().Should().Be("source fixed");
        req.GetProperty("metadata").GetProperty("previousStatus").GetString().Should().Be("DLQ");
        req.GetProperty("actor").GetProperty("type").GetString().Should().Be("CONSUMER");
        events.Count(e => e.GetProperty("type").GetString() == "DOCUMENT_STORED").Should().Be(1);

        var resolved = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=artifact&resolved=true&pageSize=200");
        resolved.GetProperty("items").EnumerateArray().Should().Contain(e => e.GetProperty("processId").GetString() == id
            && e.GetProperty("resolvedAt").ValueKind == JsonValueKind.String);
        (await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=artifact&pageSize=200")).GetProperty("items").EnumerateArray()
            .Should().NotContain(e => e.GetProperty("processId").GetString() == id);
    }

    [Fact]
    public async Task Retry_after_a_permanent_failure_with_explicit_operation_id()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 403;
        string id;
        try
        {
            id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(id, "MANUAL_ACTION");
        }
        catch { TestFixture.ToggleDown.TryRemove(key, out _); throw; }
        var failed = (await f.OpsAsync(id)).Single(o => o.GetProperty("status").GetString() == "FAILED");
        TestFixture.ToggleDown.TryRemove(key, out _);

        var resp = await f.RetryAsync(id, new { operationId = failed.GetProperty("operationId").GetString() });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
    }

    [Fact]
    public async Task Concurrent_retries_are_accepted_once()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 403;
        string id;
        try
        {
            id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(id, "MANUAL_ACTION");
        }
        catch { TestFixture.ToggleDown.TryRemove(key, out _); throw; }
        TestFixture.ToggleDown.TryRemove(key, out _);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.RetryAsync(id)));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(4);
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await f.EventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "OPERATION_REPROCESS_REQUESTED").Should().Be(1);
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Terminal_process_cannot_be_reprocessed()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await f.RetryAsync(id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Process_without_a_failed_operation_has_nothing_to_reprocess()
    {
        var id = await f.CreateProcessAsync();
        var r = await f.RetryAsync(id);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict); // healthy (or already completed) process
    }

    [Fact]
    public async Task Unknown_process_or_operation_is_404()
    {
        (await f.RetryAsync("sig_nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 403;
        try
        {
            var failedProcess = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(failedProcess, "MANUAL_ACTION");
            var other = await f.CreateProcessAsync();
            var foreignOp = (await f.OpsAsync(failedProcess)).Single(o => o.GetProperty("status").GetString() == "FAILED")
                .GetProperty("operationId").GetString();
            (await f.RetryAsync(other, new { operationId = foreignOp })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await f.RetryAsync(failedProcess, new { operationId = "op_nope" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { TestFixture.ToggleDown.TryRemove(key, out _); }
    }
}
