using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Workflow;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

[Collection("integration")]
public class WorkflowTests(TestFixture f)
{
    private static readonly string[] ExpectedOperations =
    [
        "DOCUMENT_DOWNLOAD", "DOCUMENT_STORE", "PROVIDER_CREATE_PROCESS", "PROVIDER_SEND_DOCUMENT",
        "PROVIDER_STATUS_CHECK", "SIGNED_DOCUMENT_DOWNLOAD", "SIGNED_DOCUMENT_STORE"
    ];

    [Fact]
    public async Task Process_reaches_completed_with_all_operations_and_journal_events()
    {
        var id = await f.CreateProcessAsync(signers: 2);
        var status = await f.WaitForStatusAsync(id, "COMPLETED");
        status.GetProperty("operationalStatus").GetString().Should().Be("READY");

        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations")).GetProperty("items");
        var types = ops.EnumerateArray().Select(o => o.GetProperty("type").GetString()!).ToList();
        types.Should().Contain(ExpectedOperations);
        ops.EnumerateArray().Should().OnlyContain(o => o.GetProperty("status").GetString() == "COMPLETED");
        ops.EnumerateArray().Should().OnlyContain(o => o.GetProperty("attempt").GetInt32() >= 1 && o.GetProperty("maxAttempts").GetInt32() >= 1);

        var events = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events")).GetProperty("items");
        var evTypes = events.EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();
        evTypes.Should().ContainInOrder("PROCESS_CREATED", "DOCUMENT_RECEIVED", "DOCUMENT_STORED", "PROVIDER_ACCEPTED",
            "SIGNATURE_STARTED", "SIGNATURE_COMPLETED", "SIGNED_DOCUMENT_RECEIVED", "FINAL_DOCUMENT_STORED", "PROCESS_COMPLETED");
        evTypes.Count(t => t == "SIGNER_SIGNED").Should().Be(2);
        events.EnumerateArray().Skip(1).Should().OnlyContain(e => e.GetProperty("causationId").ValueKind == JsonValueKind.String);

        var proc = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");
        proc.GetProperty("signers").EnumerateArray().Should().OnlyContain(s => s.GetProperty("signed").GetBoolean());
    }

    [Fact]
    public async Task Provider_rejection_ends_in_rejected()
    {
        var id = await f.CreateProcessAsync("SIM-REJECT-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForStatusAsync(id, "REJECTED");
    }

    [Fact]
    public async Task Provider_failure_marks_operation_failed_and_requires_manual_action()
    {
        var id = await f.CreateProcessAsync("SIM-FAIL-" + Guid.NewGuid().ToString("N")[..6]);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement st;
        do
        {
            await Task.Delay(300);
            st = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");
        } while (st.GetProperty("operationalStatus").GetString() != "MANUAL_ACTION" && DateTime.UtcNow < deadline);

        st.GetProperty("operationalStatus").GetString().Should().Be("MANUAL_ACTION");
        st.GetProperty("businessStatus").GetString().Should().Be("VALIDATING");
        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations")).GetProperty("items");
        ops.EnumerateArray().Single(o => o.GetProperty("status").GetString() == "FAILED")
            .GetProperty("type").GetString().Should().Be("PROVIDER_CREATE_PROCESS");
    }

    [Fact]
    public async Task Duplicate_message_delivery_has_no_side_effects()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var first = await db.Outbox.AsNoTracking().Where(o => o.AggregateId == id).OrderBy(o => o.CreatedAt).FirstAsync();
        var opId = JsonDocument.Parse(first.PayloadJson).RootElement.GetProperty("operationId").GetString()!;
        var opsBefore = await db.Operations.CountAsync(o => o.ProcessId == id);
        var eventsBefore = await db.Journal.CountAsync(e => e.ProcessId == id);
        var inboxBefore = await db.Inbox.CountAsync();

        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        (await engine.ExecuteAsync(first.Id, opId, "corr", null, default)).Should().Be(WorkflowOutcome.Duplicate);
        // Same operation delivered under a new message id: the operation is no longer pending, so it is ignored.
        (await engine.ExecuteAsync("msg_other_" + Guid.NewGuid().ToString("N"), opId, "corr", null, default)).Should().Be(WorkflowOutcome.Ignored);

        (await db.Operations.CountAsync(o => o.ProcessId == id)).Should().Be(opsBefore);
        (await db.Journal.CountAsync(e => e.ProcessId == id)).Should().Be(eventsBefore);
        (await db.Inbox.CountAsync()).Should().Be(inboxBefore + 1);
    }

    [Fact]
    public async Task Broker_messages_carry_only_references()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var payloads = await db.Outbox.AsNoTracking().Where(o => o.AggregateId == id).Select(o => o.PayloadJson).ToListAsync();
        payloads.Should().NotBeEmpty();
        foreach (var p in payloads)
        {
            var names = JsonDocument.Parse(p).RootElement.EnumerateObject().Select(x => x.Name).ToList();
            names.Should().BeSubsetOf(["processId", "operationId", "correlationId", "causationId", "traceparent"]);
            p.Should().NotContain("12345678909").And.NotContain("c.pdf");
        }
        (await db.Outbox.CountAsync(o => o.AggregateId == id && o.PublishedAt == null)).Should().Be(0);
    }

    [Fact]
    public async Task Broker_outage_loses_nothing_and_process_completes_after_recovery()
    {
        await f.SetBrokerPausedAsync(true);
        try
        {
            var id = await f.CreateProcessAsync();
            await Task.Delay(3000);
            var st = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");
            st.GetProperty("businessStatus").GetString().Should().Be("CREATED");
            using (var scope = f.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                (await db.Outbox.CountAsync(o => o.AggregateId == id && o.PublishedAt == null)).Should().BeGreaterThan(0);
            }
            await f.SetBrokerPausedAsync(false);
            await f.WaitForStatusAsync(id, "COMPLETED", 120);
        }
        finally { await f.SetBrokerPausedAsync(false); }
    }
}
