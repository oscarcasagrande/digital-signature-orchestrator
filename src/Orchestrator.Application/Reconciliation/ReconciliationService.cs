using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Reconciliation;

public sealed class ReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public double IntervalSeconds { get; set; } = 60;
    public double StaleAfterSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 50;
}

public static class ReconcileOutcomes
{
    public const string Corrected = "CORRECTED";
    public const string Consistent = "CONSISTENT";
    public const string NotApplicable = "NOT_APPLICABLE";
    public const string ProviderError = "PROVIDER_ERROR";
    public const string Scheduled = "SCHEDULED";
    public const string Manual = "MANUAL";
    public const string Webhook = "WEBHOOK";
}

public sealed record ReconcileResult(string ProcessId, string Outcome, string InternalStatus, string? ProviderStatus,
    bool Corrected, string? ResultingStatus, string? Error);

/// <summary>
/// Compares the internal state of a process with the provider state and fixes divergences through the state machine
/// (PRD sections 18 and 33). Concurrency with the normal flow and other instances relies on the optimistic version
/// token of the process: the loser re-reads the process and re-evaluates.
/// </summary>
public sealed class ReconciliationService(IOrchestratorDb db, IProviderAdapter provider, EventRecorder recorder, CallbackEmitter callbacks,
    IClock clock, RetryPolicy retry, IOptions<ReconciliationOptions> options, ILogger<ReconciliationService> log)
{
    public static readonly BusinessStatus[] Window =
        [BusinessStatus.READY_FOR_SIGNATURE, BusinessStatus.SIGNATURE_IN_PROGRESS, BusinessStatus.PARTIALLY_SIGNED];

    /// <summary>Processes waiting for the provider whose last activity or check is older than the staleness limit.</summary>
    public async Task<IReadOnlyList<string>> FindCandidatesAsync(CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddSeconds(-Math.Max(0, options.Value.StaleAfterSeconds));
        var window = Window;
        return await db.Processes.AsNoTracking()
            .Where(p => window.Contains(p.BusinessStatus) && (p.LastReconciledAt ?? p.UpdatedAt) < cutoff
                        && db.ProviderProcesses.Any(pp => pp.ProcessId == p.Id))
            .OrderBy(p => p.LastReconciledAt ?? p.UpdatedAt)
            .Select(p => p.Id)
            .Take(Math.Max(1, options.Value.BatchSize))
            .ToListAsync(ct);
    }

    public async Task<ReconcileResult> ReconcileAsync(string processId, string trigger, string correlationId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                return await ReconcileOnceAsync(processId, trigger, correlationId, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 12)
            {
                await Task.Delay(Random.Shared.Next(5, 40), ct); // jittered backoff so racing reconcilers do not collide forever
            }
            catch (DbUpdateException ex) when (attempt < 12 && DbErrors.IsUniqueViolation(ex)) { }
        }
    }

    private async Task<ReconcileResult> ReconcileOnceAsync(string processId, string trigger, string correlationId, CancellationToken ct)
    {
        var manual = trigger == ReconcileOutcomes.Manual;
        var p = await db.Processes.Include(x => x.Signers).FirstOrDefaultAsync(x => x.Id == processId, ct)
                ?? throw new NotFoundException("Process");
        if (p.IsTerminal) throw new ConflictException($"Process is {p.BusinessStatus} and cannot be reconciled");

        var internalStatus = p.BusinessStatus;
        // Every manual reconciliation is audited, whatever its result.
        var now = clock.UtcNow;
        var (actorType, actorId) = manual ? recorder.Api : ("SYSTEM", "reconciliation-worker");
        string? lastEvent = null;

        JournalEvent J(string type, string aType, string aId, object? meta = null) =>
            recorder.Journal(p.Id, type, aType, aId, correlationId, lastEvent, meta);
        if (manual) lastEvent = J("RECONCILIATION_REQUESTED", actorType, actorId, new { trigger }).Id;

        ReconcileResult Finish(string outcome, string? providerStatus, string? resulting, string? error, object? details)
        {
            p.LastReconciledAt = now;
            Telemetry.ReconciliationRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new("trigger", trigger));
            if (manual || outcome == ReconcileOutcomes.Corrected)
                db.ReconciliationRecords.Add(new ReconciliationRecord
                {
                    Id = Ids.New("rec"), ProcessId = p.Id, Trigger = trigger, InternalStatus = internalStatus.ToString(),
                    ProviderStatus = providerStatus, Outcome = outcome, ResultingStatus = resulting, CreatedAt = now,
                    DetailsJson = JsonSerializer.Serialize(details ?? new { })
                });
            return new ReconcileResult(p.Id, outcome, internalStatus.ToString(), providerStatus, outcome == ReconcileOutcomes.Corrected, resulting, error);
        }

        if (!Window.Contains(internalStatus) || !await db.ProviderProcesses.AnyAsync(pp => pp.ProcessId == p.Id, ct))
        {
            var na = Finish(ReconcileOutcomes.NotApplicable, null, null, null, new { reason = "no provider registration or state outside the reconciliation window" });
            await db.SaveChangesAsync(ct);
            return na;
        }

        ProviderStatusResult st;
        try
        {
            st = await provider.GetStatusAsync(p.Id, ct);
        }
        catch (OperationException ex)
        {
            log.LogWarning(ex, "Provider status query failed while reconciling process {ProcessId}", p.Id);
            var error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            var res = Finish(ReconcileOutcomes.ProviderError, null, null, error, new { error });
            if (manual) J("RECONCILIATION_PROVIDER_ERROR", actorType, actorId, new { error });
            await db.SaveChangesAsync(ct);
            return res;
        }

        var providerStatus = st.NormalizedStatus;
        BusinessStatus? target = providerStatus switch
        {
            ProviderStatuses.Signed => BusinessStatus.SIGNED,
            ProviderStatuses.PartiallySigned when internalStatus is BusinessStatus.READY_FOR_SIGNATURE or BusinessStatus.SIGNATURE_IN_PROGRESS
                => BusinessStatus.PARTIALLY_SIGNED,
            ProviderStatuses.Rejected => BusinessStatus.REJECTED,
            ProviderStatuses.Cancelled => BusinessStatus.CANCELLED,
            _ => null
        };
        var newlySigned = p.Signers.Where(s => !s.Signed && st.SignedPositions.Contains(s.Position)).OrderBy(s => s.Position).ToList();

        if (target is null && newlySigned.Count == 0)
        {
            var ok = Finish(ReconcileOutcomes.Consistent, providerStatus, internalStatus.ToString(), null, new { signedPositions = st.SignedPositions });
            await db.SaveChangesAsync(ct);
            return ok;
        }

        // Divergence found: the provider is ahead of the internal state.
        lastEvent = J("RECONCILIATION_DISCREPANCY_FOUND", actorType, actorId,
            new { internalStatus = internalStatus.ToString(), providerStatus, trigger }).Id;

        foreach (var s in newlySigned)
        {
            s.Signed = true;
            s.SignedAt = now;
            lastEvent = J("SIGNER_SIGNED", "PROVIDER", provider.Code, new { signerId = s.Id, position = s.Position, source = "reconciliation" }).Id;
        }

        async Task MoveAsync(BusinessStatus to)
        {
            p.TransitionBusiness(to, now);
            await callbacks.EmitAsync(p, to, correlationId, lastEvent, ct);
        }

        if (target is not null)
        {
            // Rejection and the signature states are only reachable through SIGNATURE_IN_PROGRESS.
            if (p.BusinessStatus == BusinessStatus.READY_FOR_SIGNATURE && target != BusinessStatus.CANCELLED)
                await MoveAsync(BusinessStatus.SIGNATURE_IN_PROGRESS);

            switch (target)
            {
                case BusinessStatus.SIGNED:
                    p.TransitionBusiness(BusinessStatus.SIGNED, now);
                    lastEvent = J("SIGNATURE_COMPLETED", "PROVIDER", provider.Code, new { source = "reconciliation" }).Id;
                    await callbacks.EmitAsync(p, BusinessStatus.SIGNED, correlationId, lastEvent, ct);
                    await EnsureSignedDocumentDownloadAsync(p, correlationId, lastEvent, now, ct);
                    break;
                case BusinessStatus.PARTIALLY_SIGNED:
                    await MoveAsync(BusinessStatus.PARTIALLY_SIGNED);
                    break;
                case BusinessStatus.REJECTED:
                    p.TransitionBusiness(BusinessStatus.REJECTED, now);
                    lastEvent = J("SIGNATURE_REJECTED", "PROVIDER", provider.Code, new { source = "reconciliation" }).Id;
                    await callbacks.EmitAsync(p, BusinessStatus.REJECTED, correlationId, lastEvent, ct);
                    break;
                case BusinessStatus.CANCELLED:
                    p.TransitionBusiness(BusinessStatus.CANCELLED, now);
                    lastEvent = J("PROCESS_CANCELLED", "PROVIDER", provider.Code, new { source = "reconciliation" }).Id;
                    await callbacks.EmitAsync(p, BusinessStatus.CANCELLED, correlationId, lastEvent, ct);
                    break;
            }

            if (target != BusinessStatus.PARTIALLY_SIGNED) await NeutralizeStatusChecksAsync(p, now, ct);
        }

        p.UpdatedAt = now;
        var resulting = p.BusinessStatus.ToString();
        J("RECONCILIATION_CORRECTED", actorType, actorId, new { from = internalStatus.ToString(), to = resulting, trigger });
        var result = Finish(ReconcileOutcomes.Corrected, providerStatus, resulting, null,
            new { signedPositions = st.SignedPositions, newlySigned = newlySigned.Select(s => s.Position) });
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task EnsureSignedDocumentDownloadAsync(SignatureProcess p, string corr, string? cause, DateTime now, CancellationToken ct)
    {
        var existing = await db.Operations.AnyAsync(o => o.ProcessId == p.Id && o.Type == OperationType.SIGNED_DOCUMENT_DOWNLOAD
                                                         && o.Status != OperationStatus.CANCELLED, ct);
        if (existing) return;
        var seq = await db.Operations.CountAsync(o => o.ProcessId == p.Id && o.Type == OperationType.SIGNED_DOCUMENT_DOWNLOAD, ct);
        var op = CreateProcessHandler.NewOperation(p.Id, OperationType.SIGNED_DOCUMENT_DOWNLOAD, seq, now,
            retry.MaxAttemptsFor(OperationType.SIGNED_DOCUMENT_DOWNLOAD));
        db.Operations.Add(op);
        recorder.EnqueueOperation(op, corr, cause, now);
    }

    /// <summary>The provider state is now known, so pending, failed or dead-lettered status checks are obsolete.</summary>
    private async Task NeutralizeStatusChecksAsync(SignatureProcess p, DateTime now, CancellationToken ct)
    {
        OperationStatus[] obsolete = [OperationStatus.NOT_STARTED, OperationStatus.RETRY_PENDING, OperationStatus.FAILED, OperationStatus.DLQ];
        var ops = await db.Operations.Where(o => o.ProcessId == p.Id && o.Type == OperationType.PROVIDER_STATUS_CHECK
                                                 && obsolete.Contains(o.Status)).ToListAsync(ct);
        foreach (var o in ops)
        {
            o.Status = OperationStatus.CANCELLED;
            o.UpdatedAt = now;
        }
        var ids = ops.Select(o => o.Id).ToList();
        if (ids.Count > 0)
            foreach (var dl in await db.DeadLetters.Where(d => ids.Contains(d.OperationId) && d.ResolvedAt == null).ToListAsync(ct))
            {
                dl.ResolvedAt = now;
                dl.ResolvedBy = "reconciliation";
            }
        if (p.OperationalStatus is OperationalStatus.RETRY_PENDING or OperationalStatus.DLQ or OperationalStatus.MANUAL_ACTION or OperationalStatus.SUSPENDED)
            p.TransitionOperational(OperationalStatus.READY, now);
    }
}

public sealed record ReconciliationDto(string ReconciliationId, string ProcessId, string Trigger, string InternalStatus, string? ProviderStatus,
    string Outcome, string? ResultingStatus, JsonElement Details, DateTime CreatedAt);

public sealed class ReconciliationQueries(IOrchestratorDb db)
{
    public async Task<IReadOnlyList<ReconciliationDto>> ListAsync(string processId, CancellationToken ct)
    {
        if (!await db.Processes.AnyAsync(p => p.Id == processId, ct)) throw new NotFoundException("Process");
        var rows = await db.ReconciliationRecords.AsNoTracking().Where(r => r.ProcessId == processId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        return rows.Select(r => new ReconciliationDto(r.Id, r.ProcessId, r.Trigger, r.InternalStatus, r.ProviderStatus, r.Outcome,
            r.ResultingStatus, JsonDocument.Parse(r.DetailsJson).RootElement.Clone(), r.CreatedAt)).ToList();
    }
}
