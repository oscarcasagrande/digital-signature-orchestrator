using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Reconciliation;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers;
using Xunit;

namespace Orchestrator.UnitTests;

public class ReconciliationServiceTests
{
    private sealed class FailingProvider : IProviderAdapter
    {
        public string Code => "SIMULATED";
        public Task<ProviderStatusResult> GetStatusAsync(string r, CancellationToken ct) => throw new ProviderException("provider unavailable", transient: true);
        public Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task SendDocumentAsync(string r, string f, CancellationToken ct) => throw new NotSupportedException();
        public Task AddSignerAsync(string r, ProviderSigner s, CancellationToken ct) => throw new NotSupportedException();
        public Task ReleaseSignerAsync(string r, int p, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SupportsPerSignerReleaseAsync(string r, CancellationToken ct) => Task.FromResult(true);
        public Task CancelAsync(string r, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderFile> DownloadSignedDocumentAsync(string r, byte[] o, string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderFile> DownloadEvidenceAsync(string r, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Env
    {
        public OrchestratorDbContext Db = null!;
        public FixedClock Clock = null!;
        public FakeProviderAdapter Fake = null!;
        public ReconciliationService Service = null!;
        public static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        public static Env Create(IProviderAdapter? providerOverride = null, double stale = 60, int batch = 50)
        {
            var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var clock = new FixedClock(T0);
            var fake = new FakeProviderAdapter(db, clock, Options.Create(new FakeProviderOptions { SignDelaySeconds = 10 }));
            var retry = new RetryPolicy(Options.Create(new RetryOptions()));
            var recorder = new EventRecorder(db, clock);
            var emitter = new CallbackEmitter(db, recorder, clock, retry);
            var svc = new ReconciliationService(db, providerOverride ?? fake, recorder, emitter, clock, retry,
                Options.Create(new ReconciliationOptions { StaleAfterSeconds = stale, BatchSize = batch }), NullLogger<ReconciliationService>.Instance);
            return new Env { Db = db, Clock = clock, Fake = fake, Service = svc };
        }

        /// <summary>A process waiting for the provider: registered and (optionally) sent there at T0.</summary>
        public async Task<SignatureProcess> ProcessAsync(string id, BusinessStatus status, int signers = 2, string externalId = "EXT-1",
            bool registered = true, bool sent = true, bool callback = false, DateTime? updatedAt = null)
        {
            var p = new SignatureProcess
            {
                Id = id, ExternalId = externalId, BusinessStatus = status, SignatureType = "ADVANCED", CorrelationId = "c",
                CreatedAt = T0, UpdatedAt = updatedAt ?? T0, RequestJson = "{}",
                CallbackJson = callback ? "{\"url\":\"https://cliente.exemplo.com/cb\"}" : null
            };
            for (var i = 0; i < signers; i++)
                p.Signers.Add(new Signer { Id = $"sgn_{id}_{i}", ProcessId = id, ExternalId = $"s{i}", Name = $"S{i}", Document = "12345678909", Position = i });
            Db.Processes.Add(p);
            if (registered)
            {
                await Fake.CreateProcessAsync(new CreateProviderProcessRequest(id, externalId, "ADVANCED",
                    p.Signers.Select(s => new ProviderSigner(s.Position, s.Name)).ToList()), default);
                await Db.SaveChangesAsync();
                if (sent) await Fake.SendDocumentAsync(id, "c.pdf", default);
            }
            await Db.SaveChangesAsync();
            return p;
        }

        public Task<ReconcileResult> Reconcile(string id, string trigger = ReconcileOutcomes.Scheduled) =>
            Service.ReconcileAsync(id, trigger, "corr", default);
    }

    [Fact]
    public async Task Provider_signed_corrects_to_signed_creates_one_download_and_notifies()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS, callback: true);
        e.Clock.UtcNow = Env.T0.AddSeconds(30);

        var r = await e.Reconcile("sig_1");
        r.Outcome.Should().Be("CORRECTED");
        (r.InternalStatus, r.ProviderStatus, r.ResultingStatus).Should().Be(("SIGNATURE_IN_PROGRESS", "SIGNED", "SIGNED"));

        var p = await e.Db.Processes.Include(x => x.Signers).SingleAsync();
        p.BusinessStatus.Should().Be(BusinessStatus.SIGNED);
        p.Signers.Should().OnlyContain(s => s.Signed);
        p.LastReconciledAt.Should().Be(e.Clock.UtcNow);

        var downloads = await e.Db.Operations.Where(o => o.Type == OperationType.SIGNED_DOCUMENT_DOWNLOAD).ToListAsync();
        downloads.Should().ContainSingle();
        (await e.Db.Outbox.CountAsync(o => o.Queue == "artifact")).Should().Be(1);

        var events = e.Db.Journal.Local.Select(j => j.Type).ToList();
        events.Should().Contain(["RECONCILIATION_DISCREPANCY_FOUND", "SIGNER_SIGNED", "SIGNATURE_COMPLETED", "RECONCILIATION_CORRECTED"]);
        events.Count(t => t == "SIGNER_SIGNED").Should().Be(2);
        (await e.Db.CallbackDeliveries.Select(d => d.EventType).ToListAsync()).Should().BeEquivalentTo(["SIGNATURE_PROCESS.SIGNED"]);
        (await e.Db.ReconciliationRecords.SingleAsync()).Outcome.Should().Be("CORRECTED");
    }

    [Fact]
    public async Task Provider_partially_signed_corrects_to_partially_signed_and_keeps_polling()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS, callback: true);
        e.Db.Operations.Add(new Operation { Id = "op_poll", ProcessId = "sig_1", Type = OperationType.PROVIDER_STATUS_CHECK, CreatedAt = Env.T0, UpdatedAt = Env.T0 });
        await e.Db.SaveChangesAsync();
        e.Clock.UtcNow = Env.T0.AddSeconds(12); // first signer signed, second not yet

        var r = await e.Reconcile("sig_1");
        r.Outcome.Should().Be("CORRECTED");
        r.ResultingStatus.Should().Be("PARTIALLY_SIGNED");
        var p = await e.Db.Processes.Include(x => x.Signers).SingleAsync();
        p.Signers.Count(s => s.Signed).Should().Be(1);
        (await e.Db.Operations.FindAsync("op_poll"))!.Status.Should().Be(OperationStatus.NOT_STARTED);
        (await e.Db.Operations.AnyAsync(o => o.Type == OperationType.SIGNED_DOCUMENT_DOWNLOAD)).Should().BeFalse();
        (await e.Db.CallbackDeliveries.Select(d => d.EventType).ToListAsync()).Should().BeEquivalentTo(["SIGNATURE_PROCESS.PARTIALLY_SIGNED"]);
    }

    [Fact]
    public async Task Provider_rejected_and_cancelled_correct_to_terminal_states()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_r", BusinessStatus.SIGNATURE_IN_PROGRESS, externalId: "SIM-REJECT-1", callback: true);
        await e.ProcessAsync("sig_c", BusinessStatus.SIGNATURE_IN_PROGRESS, externalId: "EXT-2", callback: true);
        await e.Fake.CancelAsync("sig_c", default);
        await e.Db.SaveChangesAsync();
        e.Clock.UtcNow = Env.T0.AddSeconds(15);

        (await e.Reconcile("sig_r")).ResultingStatus.Should().Be("REJECTED");
        (await e.Reconcile("sig_c")).ResultingStatus.Should().Be("CANCELLED");
        (await e.Db.Processes.FindAsync("sig_r"))!.BusinessStatus.Should().Be(BusinessStatus.REJECTED);
        (await e.Db.Processes.FindAsync("sig_c"))!.BusinessStatus.Should().Be(BusinessStatus.CANCELLED);
        (await e.Db.CallbackDeliveries.Select(d => d.EventType).ToListAsync())
            .Should().BeEquivalentTo(["SIGNATURE_PROCESS.REJECTED", "SIGNATURE_PROCESS.CANCELLED"]);
    }

    [Fact]
    public async Task Process_still_ready_for_signature_goes_through_signature_in_progress()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.READY_FOR_SIGNATURE, callback: true);
        e.Clock.UtcNow = Env.T0.AddSeconds(30);
        (await e.Reconcile("sig_1")).ResultingStatus.Should().Be("SIGNED");
        (await e.Db.CallbackDeliveries.Select(d => d.EventType).ToListAsync())
            .Should().BeEquivalentTo(["SIGNATURE_PROCESS.SIGNATURE_IN_PROGRESS", "SIGNATURE_PROCESS.SIGNED"]);
    }

    [Fact]
    public async Task Consistent_check_changes_nothing_but_the_last_check_date()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS);
        e.Clock.UtcNow = Env.T0.AddSeconds(1);
        var journalBefore = e.Db.Journal.Local.Count;

        var r = await e.Reconcile("sig_1");
        r.Outcome.Should().Be("CONSISTENT");
        r.Corrected.Should().BeFalse();
        var p = await e.Db.Processes.Include(x => x.Signers).SingleAsync();
        p.BusinessStatus.Should().Be(BusinessStatus.SIGNATURE_IN_PROGRESS);
        p.Signers.Should().OnlyContain(s => !s.Signed);
        p.UpdatedAt.Should().Be(Env.T0);
        p.LastReconciledAt.Should().Be(e.Clock.UtcNow);
        (await e.Db.ReconciliationRecords.CountAsync()).Should().Be(0);
        e.Db.Journal.Local.Count.Should().Be(journalBefore);
        (await e.Db.Operations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Manual_reconciliation_always_leaves_a_record_and_scheduled_ones_do_not_when_consistent()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS);
        e.Clock.UtcNow = Env.T0.AddSeconds(1);
        (await e.Reconcile("sig_1", ReconcileOutcomes.Scheduled)).Outcome.Should().Be("CONSISTENT");
        (await e.Db.ReconciliationRecords.CountAsync()).Should().Be(0);
        (await e.Reconcile("sig_1", ReconcileOutcomes.Manual)).Outcome.Should().Be("CONSISTENT");
        var rec = await e.Db.ReconciliationRecords.SingleAsync();
        (rec.Trigger, rec.Outcome, rec.InternalStatus).Should().Be(("MANUAL", "CONSISTENT", "SIGNATURE_IN_PROGRESS"));
    }

    [Fact]
    public async Task Obsolete_status_checks_are_neutralized_and_dead_letters_resolved()
    {
        var e = Env.Create();
        var p = await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS);
        p.OperationalStatus = OperationalStatus.DLQ;
        e.Db.Operations.AddRange(
            new Operation { Id = "op_pending", ProcessId = "sig_1", Type = OperationType.PROVIDER_STATUS_CHECK, Sequence = 0, Status = OperationStatus.NOT_STARTED, CreatedAt = Env.T0, UpdatedAt = Env.T0 },
            new Operation { Id = "op_dlq", ProcessId = "sig_1", Type = OperationType.PROVIDER_STATUS_CHECK, Sequence = 1, Status = OperationStatus.DLQ, CreatedAt = Env.T0, UpdatedAt = Env.T0 },
            new Operation { Id = "op_done", ProcessId = "sig_1", Type = OperationType.PROVIDER_SEND_DOCUMENT, Sequence = 0, Status = OperationStatus.COMPLETED, CreatedAt = Env.T0, UpdatedAt = Env.T0 });
        e.Db.DeadLetters.Add(new DeadLetterEntry { Id = "dlq_1", ProcessId = "sig_1", OperationId = "op_dlq", OperationType = "PROVIDER_STATUS_CHECK",
            Domain = "signature-provider", Queue = "signature-provider-dlq", Reason = "x", CreatedAt = Env.T0 });
        await e.Db.SaveChangesAsync();
        e.Clock.UtcNow = Env.T0.AddSeconds(30);

        (await e.Reconcile("sig_1")).Outcome.Should().Be("CORRECTED");
        (await e.Db.Operations.FindAsync("op_pending"))!.Status.Should().Be(OperationStatus.CANCELLED);
        (await e.Db.Operations.FindAsync("op_dlq"))!.Status.Should().Be(OperationStatus.CANCELLED);
        (await e.Db.Operations.FindAsync("op_done"))!.Status.Should().Be(OperationStatus.COMPLETED);
        var dl = await e.Db.DeadLetters.FindAsync("dlq_1");
        (dl!.ResolvedAt, dl.ResolvedBy).Should().Be((e.Clock.UtcNow, "reconciliation"));
        (await e.Db.Processes.FindAsync("sig_1"))!.OperationalStatus.Should().Be(OperationalStatus.READY);
    }

    [Fact]
    public async Task Reconciliation_is_idempotent()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS, callback: true);
        e.Clock.UtcNow = Env.T0.AddSeconds(30);
        (await e.Reconcile("sig_1")).Outcome.Should().Be("CORRECTED");
        // SIGNED is outside the reconciliation window now: nothing else to do and nothing duplicated.
        (await e.Reconcile("sig_1", ReconcileOutcomes.Manual)).Outcome.Should().Be("NOT_APPLICABLE");
        (await e.Reconcile("sig_1")).Outcome.Should().Be("NOT_APPLICABLE");
        (await e.Db.Operations.CountAsync(o => o.Type == OperationType.SIGNED_DOCUMENT_DOWNLOAD)).Should().Be(1);
        (await e.Db.CallbackDeliveries.CountAsync()).Should().Be(1);
        (await e.Db.ReconciliationRecords.CountAsync(r => r.Outcome == "CORRECTED")).Should().Be(1);
    }

    [Fact]
    public async Task Process_without_provider_registration_or_outside_the_window_is_not_applicable()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS, registered: false);
        await e.ProcessAsync("sig_2", BusinessStatus.VALIDATING);
        (await e.Reconcile("sig_1", ReconcileOutcomes.Manual)).Outcome.Should().Be("NOT_APPLICABLE");
        (await e.Reconcile("sig_2", ReconcileOutcomes.Manual)).Outcome.Should().Be("NOT_APPLICABLE");
        (await e.Reconcile("sig_1")).Outcome.Should().Be("NOT_APPLICABLE");
        (await e.Db.ReconciliationRecords.CountAsync()).Should().Be(2); // only the manual calls
    }

    [Fact]
    public async Task Provider_errors_are_reported_manually_and_only_logged_when_scheduled()
    {
        var e = Env.Create(new FailingProvider());
        await e.ProcessAsync("sig_1", BusinessStatus.SIGNATURE_IN_PROGRESS);
        e.Clock.UtcNow = Env.T0.AddSeconds(30);

        var scheduled = await e.Reconcile("sig_1");
        scheduled.Outcome.Should().Be("PROVIDER_ERROR");
        (await e.Db.ReconciliationRecords.CountAsync()).Should().Be(0);
        e.Db.Journal.Local.Should().NotContain(j => j.Type == "RECONCILIATION_PROVIDER_ERROR");
        (await e.Db.Processes.FindAsync("sig_1"))!.LastReconciledAt.Should().NotBeNull(); // retried after the next staleness window

        var manual = await e.Reconcile("sig_1", ReconcileOutcomes.Manual);
        manual.Outcome.Should().Be("PROVIDER_ERROR");
        manual.Error.Should().Contain("provider unavailable");
        (await e.Db.ReconciliationRecords.SingleAsync()).Outcome.Should().Be("PROVIDER_ERROR");
        e.Db.Journal.Local.Should().Contain(j => j.Type == "RECONCILIATION_PROVIDER_ERROR");
        (await e.Db.Processes.FindAsync("sig_1"))!.BusinessStatus.Should().Be(BusinessStatus.SIGNATURE_IN_PROGRESS);
    }

    [Fact]
    public async Task Terminal_and_unknown_processes_are_rejected()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.COMPLETED);
        await Assert.ThrowsAsync<ConflictException>(() => e.Reconcile("sig_1", ReconcileOutcomes.Manual));
        await Assert.ThrowsAsync<NotFoundException>(() => e.Reconcile("sig_nope", ReconcileOutcomes.Manual));
    }

    [Fact]
    public async Task Provider_behind_the_internal_state_is_consistent()
    {
        var e = Env.Create();
        await e.ProcessAsync("sig_1", BusinessStatus.PARTIALLY_SIGNED);
        // internal state says partially signed (but no signer flagged); provider still pending at +1s
        e.Clock.UtcNow = Env.T0.AddSeconds(1);
        (await e.Reconcile("sig_1")).Outcome.Should().Be("CONSISTENT");
        (await e.Db.Processes.FindAsync("sig_1"))!.BusinessStatus.Should().Be(BusinessStatus.PARTIALLY_SIGNED);
    }

    [Fact]
    public async Task Candidates_are_stale_waiting_processes_with_a_provider_registration_oldest_first_within_the_batch()
    {
        var e = Env.Create(stale: 60, batch: 2);
        e.Clock.UtcNow = Env.T0.AddMinutes(10);
        await e.ProcessAsync("sig_old", BusinessStatus.SIGNATURE_IN_PROGRESS, updatedAt: Env.T0.AddMinutes(1));
        await e.ProcessAsync("sig_older", BusinessStatus.PARTIALLY_SIGNED, updatedAt: Env.T0);
        await e.ProcessAsync("sig_oldest_in_window_but_third", BusinessStatus.READY_FOR_SIGNATURE, updatedAt: Env.T0.AddMinutes(2));
        await e.ProcessAsync("sig_recent", BusinessStatus.SIGNATURE_IN_PROGRESS, updatedAt: Env.T0.AddMinutes(9).AddSeconds(30));
        await e.ProcessAsync("sig_other_state", BusinessStatus.SIGNED, updatedAt: Env.T0);
        await e.ProcessAsync("sig_no_provider", BusinessStatus.SIGNATURE_IN_PROGRESS, registered: false, updatedAt: Env.T0);

        (await e.Service.FindCandidatesAsync(default)).Should().Equal("sig_older", "sig_old"); // batch of 2, oldest first

        // A recent check removes a process from the candidates until the limit passes again.
        var p = await e.Db.Processes.FindAsync("sig_older");
        p!.LastReconciledAt = e.Clock.UtcNow;
        await e.Db.SaveChangesAsync();
        (await e.Service.FindCandidatesAsync(default)).Should().Equal("sig_old", "sig_oldest_in_window_but_third");
    }
}
