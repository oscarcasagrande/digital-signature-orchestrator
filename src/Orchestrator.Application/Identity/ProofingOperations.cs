using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Security;
using Orchestrator.Application.Workflow;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Identity;

public sealed record ValidationSummaryDto(string Type, bool Required, string Status);
public sealed record EvidenceDto(string Type, string ContentType, string Sha256, long Size, DateTime ReceivedAt);
public sealed record ProofingSessionDto(string SessionId, string ExternalId, string Status, string Result, object Subject,
    IReadOnlyList<ValidationSummaryDto> Validations, IReadOnlyList<EvidenceDto> Evidence,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt, DateTime? EvidenceDeletedAt);
public sealed record ValidationResultDto(string Type, bool Required, string Status, double? Score, JsonElement Details, DateTime? CompletedAt);
public sealed record ProofingResultDto(string SessionId, string Status, string Result, IReadOnlyList<ValidationResultDto> Validations);

public sealed class ProofingQueries(IOrchestratorDb db)
{
    public async Task<ProofingSessionDto> GetAsync(string id, CancellationToken ct)
    {
        var s = await db.ProofingSessions.AsNoTracking().Include(x => x.Validations).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Proofing session");
        var types = IdentityCapabilities.EvidenceTypes;
        var evidence = await db.Artifacts.AsNoTracking().Where(a => a.ProcessId == id && types.Contains(a.Type)).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        using var subject = JsonDocument.Parse(s.SubjectJson);
        var sj = subject.RootElement;
        string? P(string n) => sj.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new ProofingSessionDto(s.Id, s.ExternalId, s.Status.ToString(), s.Result.ToString(),
            new { name = P("name"), document = SensitiveMasker.MaskDocument(P("document")), phone = SensitiveMasker.MaskPhone(P("phone")),
                  email = SensitiveMasker.MaskEmail(P("email")), deviceId = P("deviceId") },
            s.Validations.OrderBy(v => v.Capability).Select(v => new ValidationSummaryDto(v.Capability, v.Required, v.Status.ToString())).ToList(),
            evidence.Select(a => new EvidenceDto(a.Type.ToString(), a.ContentType, a.Sha256, a.Size, a.CreatedAt)).ToList(),
            s.CreatedAt, s.UpdatedAt, s.CompletedAt, s.EvidenceDeletedAt);
    }

    public async Task<ProofingResultDto> ResultAsync(string id, CancellationToken ct)
    {
        var s = await db.ProofingSessions.AsNoTracking().Include(x => x.Validations).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Proofing session");
        return new ProofingResultDto(s.Id, s.Status.ToString(), s.Result.ToString(),
            s.Validations.OrderBy(v => v.Capability).Select(v => new ValidationResultDto(v.Capability, v.Required, v.Status.ToString(), v.Score,
                JsonDocument.Parse(v.DetailsJson).RootElement.Clone(), v.CompletedAt)).ToList());
    }

    public async Task<PagedResult<OperationDto>> OperationsAsync(string id, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.ProofingSessions.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Proofing session");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, ProcessQueries.MaxPageSize);
        var q = db.Operations.AsNoTracking().Where(o => o.ProcessId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(o => o.CreatedAt).ThenBy(o => o.Sequence).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<OperationDto>(rows.Select(o => new OperationDto(o.Id, o.ProcessId, o.Type.ToString(), o.Status.ToString(), o.Attempt,
            o.MaxAttempts, o.NextRetryAt, o.OutputJson is null ? null : JsonDocument.Parse(o.OutputJson).RootElement.Clone(),
            o.ErrorJson is null ? null : JsonDocument.Parse(o.ErrorJson).RootElement.Clone(), o.CreatedAt, o.UpdatedAt)).ToList(), page, pageSize, total);
    }

    public async Task<PagedResult<EventDto>> EventsAsync(string id, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.ProofingSessions.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Proofing session");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, ProcessQueries.MaxPageSize);
        var q = db.Journal.AsNoTracking().Where(e => e.ProcessId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(e => e.Seq).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<EventDto>(rows.Select(e => new EventDto(e.Id, e.ProcessId, e.Type, e.OccurredAt,
            new { type = e.ActorType, id = e.ActorId }, JsonDocument.Parse(e.MetadataJson).RootElement.Clone(), e.CorrelationId, e.CausationId)).ToList(),
            page, pageSize, total);
    }
}

/// <summary>Resumes a proofing session from the validation that failed, without repeating finished validations.</summary>
public sealed class ReprocessIdentityHandler(IOrchestratorDb db, EventRecorder recorder, IClock clock)
{
    private static readonly OperationStatus[] Reprocessable = [OperationStatus.FAILED, OperationStatus.DLQ, OperationStatus.RETRY_PENDING];

    public async Task<ReprocessResult> HandleAsync(string sessionId, ReprocessRequest? request, string correlationId, CancellationToken ct)
    {
        if (request?.Reason is { Length: > 500 }) throw new ValidationException([new FieldError("reason", "Max length is 500")]);
        await using var tx = await ProofingCore.LockSessionAsync(db, sessionId, ct);
        var session = await db.ProofingSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException("Proofing session");
        if (session.Status == ProofingSessionStatus.COMPLETED) throw new ConflictException("The session is completed and cannot be reprocessed");

        var ops = db.Operations.Where(o => o.ProcessId == sessionId && o.Type == OperationType.IDENTITY_VALIDATION);
        Operation op;
        if (!string.IsNullOrWhiteSpace(request?.OperationId))
        {
            op = await ops.FirstOrDefaultAsync(o => o.Id == request.OperationId, ct) ?? throw new NotFoundException("Operation");
            if (!Reprocessable.Contains(op.Status)) throw new ConflictException($"Operation is {op.Status} and cannot be reprocessed");
        }
        else
        {
            op = await ops.Where(o => Reprocessable.Contains(o.Status)).OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Sequence)
                     .FirstOrDefaultAsync(ct) ?? throw new ConflictException("The session has no validation to reprocess");
        }

        var now = clock.UtcNow;
        var previous = op.Status;
        op.Status = OperationStatus.NOT_STARTED;
        op.Attempt = 0;
        op.ErrorJson = null;
        op.ErrorClass = null;
        op.NextRetryAt = null;
        op.UpdatedAt = now;
        var validation = await db.IdentityValidations.FirstOrDefaultAsync(v => v.OperationId == op.Id, ct);
        if (validation is not null) { validation.Status = ValidationStatus.PENDING; validation.UpdatedAt = now; }
        foreach (var dl in await db.DeadLetters.Where(d => d.OperationId == op.Id && d.ResolvedAt == null).ToListAsync(ct))
        {
            dl.ResolvedAt = now;
            dl.ResolvedBy = "api";
        }
        session.UpdatedAt = now;
        var ev = recorder.Journal(sessionId, "OPERATION_REPROCESS_REQUESTED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
            new { operationId = op.Id, type = op.Type.ToString(), previousStatus = previous.ToString(), reason = request?.Reason,
                  capability = validation?.Capability });
        recorder.EnqueueOperation(op, correlationId, ev.Id, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReprocessResult(sessionId, op.Id, op.Type.ToString(), session.Status.ToString());
    }
}

/// <summary>Deletes biometric and document evidence (manual deletion and retention policy).</summary>
public sealed class EvidencePurger(IOrchestratorDb db, IArtifactStore store, EventRecorder recorder, IClock clock,
    IOptions<IdentityOptions> options, ILogger<EvidencePurger> log)
{
    /// <returns>Number of evidence items removed.</returns>
    public async Task<int> PurgeAsync(string sessionId, string reason, string correlationId, bool requireCompleted, CancellationToken ct)
    {
        await using var tx = await ProofingCore.LockSessionAsync(db, sessionId, ct);
        var session = await db.ProofingSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException("Proofing session");
        if (requireCompleted && session.Status != ProofingSessionStatus.COMPLETED)
            throw new ConflictException("Evidence can only be deleted after the session is completed");

        var types = IdentityCapabilities.EvidenceTypes;
        var items = await db.Artifacts.Where(a => a.ProcessId == sessionId && types.Contains(a.Type)).ToListAsync(ct);
        if (items.Count == 0) return 0;

        var summary = items.Select(a => new { type = a.Type.ToString(), sha256 = a.Sha256 }).ToList();
        foreach (var a in items) await store.DeleteAsync(a.StorageKey, ct);
        db.Artifacts.RemoveRange(items);
        var now = clock.UtcNow;
        session.EvidenceDeletedAt = now;
        session.UpdatedAt = now;
        var (purgeActorType, purgeActorId) = reason == "retention" ? ("SYSTEM", "retention-policy") : recorder.Api;
        recorder.Journal(sessionId, "EVIDENCE_DELETED", purgeActorType, purgeActorId, correlationId, null,
            new { reason, evidence = summary });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return items.Count;
    }

    /// <summary>Applies the retention policy: completed sessions older than the retention still holding evidence.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-Math.Max(0, options.Value.EvidenceRetentionDays));
        var types = IdentityCapabilities.EvidenceTypes;
        var ids = await db.ProofingSessions.AsNoTracking()
            .Where(s => s.Status == ProofingSessionStatus.COMPLETED && s.CompletedAt != null && s.CompletedAt < cutoff
                        && db.Artifacts.Any(a => a.ProcessId == s.Id && types.Contains(a.Type)))
            .Select(s => s.Id).Take(100).ToListAsync(ct);
        var total = 0;
        foreach (var id in ids)
        {
            try { total += await PurgeAsync(id, "retention", "retention-" + Guid.NewGuid().ToString("N")[..12], requireCompleted: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Evidence retention purge failed for session {SessionId}", id);
                db.ChangeTracker.Clear();
            }
        }
        return total;
    }
}
