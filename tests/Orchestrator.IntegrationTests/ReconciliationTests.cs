using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Artifacts;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class ReconSeed
{
    /// <summary>
    /// Builds the "lost webhook / lost polling" situation directly in the database: a process waiting for the provider,
    /// registered there and (by default) already signed by every signer, with no follow-up operation scheduled.
    /// </summary>
    public static async Task<string> SeedAsync(this TestFixture f, BusinessStatus status = BusinessStatus.SIGNATURE_IN_PROGRESS, int signers = 1,
        string? callbackJson = null, double sentSecondsAgo = 30, bool providerRegistration = true, bool withOriginal = true,
        string? metadataOverride = null, string? externalId = null, int updatedSecondsAgo = 120)
    {
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var id = "sig_seed" + Guid.NewGuid().ToString("N")[..16];
        var now = DateTime.UtcNow;
        var ext = externalId ?? "SEED-" + id[^6..];
        var p = new SignatureProcess
        {
            Id = id, ExternalId = ext, BusinessStatus = status, SignatureType = "ADVANCED", CorrelationId = "seed", CallbackJson = callbackJson,
            RequestJson = "{\"externalId\":\"" + ext + "\",\"document\":{\"fileName\":\"seed.pdf\",\"source\":{\"type\":\"URL\",\"url\":\"" + TestFixture.DocumentUrl + "\"}}}",
            CreatedAt = now.AddSeconds(-updatedSecondsAgo - 5), UpdatedAt = now.AddSeconds(-updatedSecondsAgo)
        };
        for (var i = 0; i < signers; i++)
            p.Signers.Add(new Signer { Id = $"sgn_{id}_{i}", ProcessId = id, ExternalId = $"s{i}", Name = $"Signer {i}", Document = "12345678909", Position = i });
        db.Processes.Add(p);
        if (providerRegistration)
        {
            var sentAt = now.AddSeconds(-sentSecondsAgo);
            var meta = metadataOverride ?? "{\"Mode\":\"Auto\",\"Signers\":" + signers + ",\"ExternalId\":\"" + ext + "\",\"SentAt\":\""
                       + sentAt.ToString("o") + "\",\"Status\":\"PENDING\",\"Cancelled\":false}";
            db.ProviderProcesses.Add(new ProviderProcess
            {
                ProcessId = id, ProviderCode = "SIMULATED", ProviderProcessId = "sim_" + id[^8..], ExternalReference = id,
                NormalizedStatus = "PENDING", MetadataJson = meta, UpdatedAt = now
            });
        }
        if (withOriginal)
        {
            var key = ArtifactKeys.For(id, ArtifactType.ORIGINAL_DOCUMENT);
            await store.PutAsync(key, TestFixture.DocBytes, "application/pdf", default);
            db.Artifacts.Add(new Artifact
            {
                Id = "art_" + id[^12..], ProcessId = id, Type = ArtifactType.ORIGINAL_DOCUMENT, ContentType = "application/pdf",
                Sha256 = Sha.Hex(TestFixture.DocBytes), Size = TestFixture.DocBytes.Length, StorageKey = key, FileName = "seed.pdf", CreatedAt = now
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<List<JsonElement>> ReconciliationsAsync(this TestFixture f, string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/reconciliations")).GetProperty("items").EnumerateArray().ToList();

    public static async Task<HttpResponseMessage> ReconcileAsync(this TestFixture f, string id) =>
        await f.Client.PostAsync($"/v1/signature-processes/{id}/reconcile", null);
}

/// <summary>Scheduled reconciliation: a fast reconciler host runs while these tests execute.</summary>
[Collection("integration")]
public class ReconciliationTests(TestFixture f) : IAsyncLifetime
{
    private IAsyncDisposable _reconciler = default!;

    public async Task InitializeAsync() => _reconciler = await f.StartReconcilerAsync();
    public async Task DisposeAsync() => await _reconciler.DisposeAsync();

    [Fact]
    public async Task Divergent_process_is_corrected_and_the_workflow_continues_to_completion_with_callbacks()
    {
        var key = "rec" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.SeedAsync(signers: 2, callbackJson: f.DynamicCallback(key));

        await f.WaitForStatusAsync(id, "COMPLETED", 60);

        var artifacts = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items");
        artifacts.EnumerateArray().Select(a => a.GetProperty("type").GetString()).Should().BeEquivalentTo("ORIGINAL_DOCUMENT", "SIGNED_DOCUMENT", "EVIDENCE");
        var proc = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");
        proc.GetProperty("signers").EnumerateArray().Should().OnlyContain(s => s.GetProperty("signed").GetBoolean());

        var events = await f.EventsAsync(id);
        var types = events.Select(e => e.GetProperty("type").GetString()).ToList();
        types.Should().Contain(["RECONCILIATION_DISCREPANCY_FOUND", "RECONCILIATION_CORRECTED", "SIGNATURE_COMPLETED", "PROCESS_COMPLETED"]);
        types.Count(t => t == "RECONCILIATION_CORRECTED").Should().Be(1);
        events.First(e => e.GetProperty("type").GetString() == "RECONCILIATION_CORRECTED").GetProperty("actor").GetProperty("id").GetString()
            .Should().Be("reconciliation-worker");

        var records = await f.ReconciliationsAsync(id);
        var rec = records.Should().ContainSingle().Subject;
        (rec.GetProperty("trigger").GetString(), rec.GetProperty("outcome").GetString(), rec.GetProperty("internalStatus").GetString(),
            rec.GetProperty("providerStatus").GetString(), rec.GetProperty("resultingStatus").GetString())
            .Should().Be(("SCHEDULED", "CORRECTED", "SIGNATURE_IN_PROGRESS", "SIGNED", "SIGNED"));

        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 2 && d.All(x => x.GetProperty("status").GetString() == "DELIVERED"));
        deliveries.Select(d => d.GetProperty("eventType").GetString()).Should().BeEquivalentTo("SIGNATURE_PROCESS.SIGNED", "SIGNATURE_PROCESS.COMPLETED");
        TestFixture.ReceivedFor(key).Should().OnlyContain(r => r.SignatureValid);
    }

    [Fact]
    public async Task Obsolete_status_check_in_dlq_is_neutralized_and_the_process_recovers()
    {
        var id = await f.SeedAsync();
        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var now = DateTime.UtcNow;
            db.Operations.Add(new Operation { Id = "op_stuck_" + id[^8..], ProcessId = id, Type = OperationType.PROVIDER_STATUS_CHECK, Status = OperationStatus.DLQ,
                Attempt = 4, MaxAttempts = 4, CreatedAt = now, UpdatedAt = now });
            db.DeadLetters.Add(new DeadLetterEntry { Id = "dlq_stuck_" + id[^8..], ProcessId = id, OperationId = "op_stuck_" + id[^8..],
                OperationType = "PROVIDER_STATUS_CHECK", Domain = "signature-provider", Queue = "signature-provider-dlq", ErrorClass = ErrorClass.TRANSIENT,
                Reason = "provider unavailable", Attempts = 4, CreatedAt = now });
            var p = await db.Processes.FirstAsync(x => x.Id == id);
            p.OperationalStatus = OperationalStatus.DLQ;
            await db.SaveChangesAsync();
        }

        await f.WaitForStatusAsync(id, "COMPLETED", 60);
        var st = await f.StatusAsync(id);
        st.GetProperty("operationalStatus").GetString().Should().Be("READY");
        (await f.OpsAsync(id)).Single(o => o.GetProperty("type").GetString() == "PROVIDER_STATUS_CHECK").GetProperty("status").GetString().Should().Be("CANCELLED");
        var open = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=signature-provider&pageSize=200");
        open.GetProperty("items").EnumerateArray().Should().NotContain(e => e.GetProperty("processId").GetString() == id);
        var resolved = await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?domain=signature-provider&resolved=true&pageSize=200");
        resolved.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("processId").GetString() == id).GetProperty("resolvedAt").ValueKind
            .Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Consistent_process_is_not_changed_and_leaves_no_trace_besides_the_last_check_date()
    {
        var id = await f.SeedAsync(sentSecondsAgo: -3600); // provider has not signed yet
        var eventsBefore = (await f.EventsAsync(id)).Count;
        await Task.Delay(2500); // several sweeps (0.3 s interval, 0.5 s staleness)

        (await f.StatusAsync(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        (await f.ReconciliationsAsync(id)).Should().BeEmpty();
        (await f.EventsAsync(id)).Count.Should().Be(eventsBefore);
        using var scope = f.Factory.Services.CreateScope();
        var p = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Processes.AsNoTracking().FirstAsync(x => x.Id == id);
        p.LastReconciledAt.Should().NotBeNull();
        p.BusinessStatus.Should().Be(BusinessStatus.SIGNATURE_IN_PROGRESS);
    }

    [Fact]
    public async Task A_failing_candidate_does_not_stop_the_other_processes_of_the_cycle()
    {
        var poison = await f.SeedAsync(metadataOverride: "[]", updatedSecondsAgo: 600); // corrupt provider data: querying it throws
        var good = await f.SeedAsync(updatedSecondsAgo: 60);
        await f.WaitForStatusAsync(good, "COMPLETED", 60);
        (await f.StatusAsync(poison)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
    }

    [Fact]
    public async Task Process_without_provider_registration_is_never_a_candidate()
    {
        var id = await f.SeedAsync(providerRegistration: false);
        await Task.Delay(2000);
        (await f.StatusAsync(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        (await f.ReconciliationsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Process_outside_the_waiting_states_is_never_a_candidate()
    {
        var id = await f.SeedAsync(status: BusinessStatus.VALIDATING);
        await Task.Delay(2000);
        (await f.StatusAsync(id)).GetProperty("businessStatus").GetString().Should().Be("VALIDATING");
        (await f.ReconciliationsAsync(id)).Should().BeEmpty();
    }
}

/// <summary>Manual reconciliation and history (no scheduled reconciler running).</summary>
[Collection("integration")]
public class ReconciliationApiTests(TestFixture f)
{
    [Fact]
    public async Task Manual_reconciliation_corrects_a_divergent_process_and_records_it()
    {
        var id = await f.SeedAsync();
        var resp = await f.ReconcileAsync(id);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("outcome").GetString().Should().Be("CORRECTED");
        r.GetProperty("corrected").GetBoolean().Should().BeTrue();
        r.GetProperty("internalStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        r.GetProperty("providerStatus").GetString().Should().Be("SIGNED");
        r.GetProperty("resultingStatus").GetString().Should().Be("SIGNED");

        await f.WaitForStatusAsync(id, "COMPLETED"); // the normal worker continues the workflow

        var rec = (await f.ReconciliationsAsync(id)).Should().ContainSingle().Subject;
        rec.GetProperty("trigger").GetString().Should().Be("MANUAL");
        rec.GetProperty("outcome").GetString().Should().Be("CORRECTED");
        var events = await f.EventsAsync(id);
        var corrected = events.First(e => e.GetProperty("type").GetString() == "RECONCILIATION_CORRECTED");
        corrected.GetProperty("actor").GetProperty("type").GetString().Should().Be("CONSUMER");
        corrected.GetProperty("metadata").GetProperty("to").GetString().Should().Be("SIGNED");
    }

    [Fact]
    public async Task Manual_reconciliation_of_a_consistent_process_changes_nothing_but_is_recorded()
    {
        var id = await f.SeedAsync(sentSecondsAgo: -3600);
        var r = await (await f.ReconcileAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("outcome").GetString().Should().Be("CONSISTENT");
        r.GetProperty("corrected").GetBoolean().Should().BeFalse();
        r.GetProperty("providerStatus").GetString().Should().Be("PENDING");
        (await f.StatusAsync(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        var rec = (await f.ReconciliationsAsync(id)).Should().ContainSingle().Subject;
        rec.GetProperty("outcome").GetString().Should().Be("CONSISTENT");
        (await f.EventsAsync(id)).Should().NotContain(e => e.GetProperty("type").GetString() == "RECONCILIATION_CORRECTED");
    }

    [Fact]
    public async Task Not_applicable_conflict_and_not_found()
    {
        var noProvider = await f.SeedAsync(providerRegistration: false);
        var na = await (await f.ReconcileAsync(noProvider)).Content.ReadFromJsonAsync<JsonElement>();
        na.GetProperty("outcome").GetString().Should().Be("NOT_APPLICABLE");
        na.GetProperty("providerStatus").ValueKind.Should().Be(JsonValueKind.Null);

        var done = await f.SeedAsync(status: BusinessStatus.COMPLETED);
        (await f.ReconcileAsync(done)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await f.ReconcileAsync("sig_nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await f.Client.GetAsync("/v1/signature-processes/sig_nope/reconciliations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_lists_manual_checks_in_chronological_order()
    {
        var id = await f.SeedAsync(sentSecondsAgo: -3600);
        await f.ReconcileAsync(id);
        await f.ReconcileAsync(id);
        var records = await f.ReconciliationsAsync(id);
        records.Should().HaveCount(2);
        records.Select(r => r.GetProperty("createdAt").GetDateTime()).Should().BeInAscendingOrder();
        records.Should().OnlyContain(r => r.GetProperty("trigger").GetString() == "MANUAL");
    }
}

/// <summary>Reconciliation racing with itself and with the normal flow.</summary>
[Collection("integration")]
public class ReconciliationConcurrencyTests(TestFixture f)
{
    [Fact]
    public async Task Concurrent_reconciliations_of_one_divergent_process_correct_it_exactly_once()
    {
        var key = "conc" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.SeedAsync(callbackJson: f.DynamicCallback(key));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.ReconcileAsync(id)));
        var ok = new List<JsonElement>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.OK)) ok.Add(await r.Content.ReadFromJsonAsync<JsonElement>());
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        ok.Count(r => r.GetProperty("outcome").GetString() == "CORRECTED").Should().Be(1);
        ok.Where(r => r.GetProperty("outcome").GetString() != "CORRECTED").Should().OnlyContain(r => r.GetProperty("outcome").GetString() == "NOT_APPLICABLE");

        await f.WaitForStatusAsync(id, "COMPLETED");
        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.Count >= 2 && d.All(x => x.GetProperty("status").GetString() == "DELIVERED"));
        var ops = await f.OpsAsync(id);
        ops.Count(o => o.GetProperty("type").GetString() == "SIGNED_DOCUMENT_DOWNLOAD").Should().Be(1);
        ops.Should().OnlyContain(o => o.GetProperty("status").GetString() == "COMPLETED" || o.GetProperty("status").GetString() == "CANCELLED");

        deliveries.Select(d => d.GetProperty("eventType").GetString()).Should().BeEquivalentTo("SIGNATURE_PROCESS.SIGNED", "SIGNATURE_PROCESS.COMPLETED");
        (await f.ReconciliationsAsync(id)).Count(r => r.GetProperty("outcome").GetString() == "CORRECTED").Should().Be(1);
        (await f.EventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "SIGNATURE_COMPLETED").Should().Be(1);
    }

    [Fact]
    public async Task Reconciling_while_the_normal_flow_runs_never_breaks_the_process()
    {
        var key = "race" + Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync(signers: 2, callbackJson: f.DynamicCallback(key));

        var deadline = DateTime.UtcNow.AddSeconds(60);
        var failures = new List<HttpStatusCode>();
        while (DateTime.UtcNow < deadline)
        {
            var r = await f.ReconcileAsync(id);
            if (r.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict)) failures.Add(r.StatusCode);
            var st = await f.StatusAsync(id);
            if (st.GetProperty("businessStatus").GetString() == "COMPLETED") break;
            await Task.Delay(80);
        }
        failures.Should().BeEmpty();
        await f.WaitForStatusAsync(id, "COMPLETED");

        var ops = await f.OpsAsync(id);
        ops.Should().NotContain(o => o.GetProperty("status").GetString() == "FAILED" || o.GetProperty("status").GetString() == "DLQ");
        ops.Count(o => o.GetProperty("type").GetString() == "SIGNED_DOCUMENT_DOWNLOAD" && o.GetProperty("status").GetString() == "COMPLETED").Should().Be(1);
        (await f.EventsAsync(id)).Count(e => e.GetProperty("type").GetString() == "SIGNATURE_COMPLETED").Should().Be(1);
        (await f.StatusAsync(id)).GetProperty("operationalStatus").GetString().Should().Be("READY");
        var deliveries = await f.WaitForDeliveriesAsync(id, d => d.All(x => x.GetProperty("status").GetString() == "DELIVERED") && d.Any(x => x.GetProperty("eventType").GetString() == "SIGNATURE_PROCESS.COMPLETED"));
        deliveries.Select(d => d.GetProperty("eventType").GetString()).Distinct().Count().Should().Be(deliveries.Count); // one delivery per status
    }

    [Fact]
    public async Task Repeated_reconciliation_after_the_fix_is_a_no_op()
    {
        var id = await f.SeedAsync();
        (await (await f.ReconcileAsync(id)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().Should().Be("CORRECTED");
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await f.ReconcileAsync(id)).StatusCode.Should().Be(HttpStatusCode.Conflict); // terminal now
        (await f.ReconciliationsAsync(id)).Count(r => r.GetProperty("outcome").GetString() == "CORRECTED").Should().Be(1);
    }
}
