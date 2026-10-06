using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Security;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Processes;

public sealed class NotFoundException(string what) : Exception($"{what} not found");

public sealed record ConfirmationDto(string Channel, string Status, int SendCount, DateTime? LastSentAt, DateTime? ExpiresAt,
    int? AttemptsRemaining, DateTime? ConfirmedAt);

public sealed record SignerDto(string Id, string ExternalId, string Name, string Document, bool Signed, DateTime? SignedAt,
    string? Email = null, string? Phone = null, string? SignatureType = null, int? Order = null,
    IReadOnlyList<ConfirmationDto>? Confirmations = null, DateTime? ReleasedAt = null);

public sealed record ProcessDto(
    string ProcessId, string ExternalId, string BusinessStatus, string OperationalStatus, string SignatureType,
    IReadOnlyList<SignerDto> Signers, JsonElement? Callback, JsonElement? IdentityValidations,
    string CorrelationId, DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt,
    string? DocumentFileName, string? Provider, string Sla, ProgressDetailDto Progress);

public sealed record ProcessListItem(string ProcessId, string ExternalId, string? DocumentFileName, string? Provider, int SignersSigned,
    int SignersTotal, string BusinessStatus, string OperationalStatus, DateTime CreatedAt, DateTime UpdatedAt, string Sla, ProgressDto Progress);

public sealed record ProviderInfoDto(string Provider, string ProviderProcessId, string ExternalReference, string NormalizedStatus,
    JsonElement Metadata, DateTime UpdatedAt);

/// <summary>Simple SLA indicator (basic version; SLA management is Phase 2).</summary>
public static class SlaCalculator
{
    public static string For(BusinessStatus business, OperationalStatus operational, DateTime createdAt, DateTime now, int signingMinutes)
    {
        if (business is BusinessStatus.FAILED or BusinessStatus.REJECTED or BusinessStatus.EXPIRED) return "ALERT";
        if (operational is OperationalStatus.DLQ or OperationalStatus.MANUAL_ACTION) return "ALERT";
        if (!BusinessStateMachine.IsTerminal(business) && now - createdAt > TimeSpan.FromMinutes(signingMinutes)) return "ALERT";
        return "OK";
    }
}

public sealed record StatusDto(string ProcessId, string BusinessStatus, string OperationalStatus, DateTime UpdatedAt);

public sealed record OperationDto(string OperationId, string ProcessId, string Type, string Status, int Attempt,
    int MaxAttempts, DateTime? NextRetryAt, JsonElement? Output, JsonElement? Error, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record EventDto(string EventId, string ProcessId, string Type, DateTime Timestamp,
    object Actor, JsonElement Metadata, string CorrelationId, string? CausationId);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed class ProcessQueries(IOrchestratorDb db, IClock clock, Microsoft.Extensions.Options.IOptions<SlaOptions> sla, EventRecorder recorder,
    Microsoft.Extensions.Options.IOptions<Confirmation.ConfirmationOptions> confirmationOptions, CallerContext? caller = null)
{
    public const int MaxPageSize = 200;

    public async Task<ProcessDto> GetAsync(string id, CancellationToken ct)
    {
        var p = await db.Processes.AsNoTracking().Include(x => x.Signers).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Process");
        var provider = await db.ProviderProcesses.AsNoTracking().Where(pp => pp.ProcessId == id).Select(pp => pp.ProviderCode).FirstOrDefaultAsync(ct);
        var now = clock.UtcNow;
        var confirmations = (await db.Confirmations.AsNoTracking().Where(c => c.ProcessId == id).OrderBy(c => c.Id).ToListAsync(ct))
            .GroupBy(c => c.SignerId).ToDictionary(g => g.Key, g => g.ToList());
        var progress = (await ProgressAsync([(p.Id, p.BusinessStatus, p.CallbackJson is not null)], ct))[p.Id];
        return new ProcessDto(p.Id, p.ExternalId, p.BusinessStatus.ToString(), p.OperationalStatus.ToString(), p.SignatureType,
            p.Signers.OrderBy(s => s.Position)
                .Select(s => new SignerDto(s.Id, s.ExternalId, s.Name, SensitiveMasker.MaskDocument(s.Document), s.Signed, s.SignedAt,
                    SensitiveMasker.MaskEmail(s.Email), SensitiveMasker.MaskPhone(s.Phone), s.SignatureType, s.Order,
                    confirmations.GetValueOrDefault(s.Id, []).Select(c => ToDto(c, now)).ToList(), s.ReleasedAt)).ToList(),
            Parse(p.CallbackJson), Parse(p.IdentityValidationsJson), p.CorrelationId, p.CreatedAt, p.UpdatedAt, p.CompletedAt,
            p.DocumentFileName, provider, SlaCalculator.For(p.BusinessStatus, p.OperationalStatus, p.CreatedAt, now, sla.Value.SigningMinutes), progress);
    }

    private ConfirmationDto ToDto(SignerConfirmation c, DateTime now) => new(c.Channel,
        c.Status == ConfirmationStatus.SENT && c.ExpiresAt <= now ? "EXPIRED" : c.Status.ToString(), c.SendCount, c.LastSentAt, c.ExpiresAt,
        c.Status == ConfirmationStatus.SENT ? Math.Max(0, confirmationOptions.Value.MaxAttempts - c.FailedAttempts) : null, c.ConfirmedAt);

    /// <summary>Loads the facts of the given processes in batch (one query per table) and computes their progress.</summary>
    private async Task<Dictionary<string, ProgressDetailDto>> ProgressAsync(IReadOnlyList<(string Id, BusinessStatus Business, bool HasCallback)> procs, CancellationToken ct)
    {
        var ids = procs.Select(p => p.Id).ToList();
        var signers = (await db.Signers.AsNoTracking().Where(s => ids.Contains(s.ProcessId))
            .Select(s => new { s.ProcessId, s.Id, s.Name, s.Position, s.Order, s.Signed, s.ReleasedAt }).ToListAsync(ct)).ToLookup(s => s.ProcessId);
        var confs = (await db.Confirmations.AsNoTracking().Where(c => ids.Contains(c.ProcessId)).OrderBy(c => c.Id)
            .Select(c => new { c.ProcessId, c.SignerId, c.Channel, c.Status }).ToListAsync(ct)).ToLookup(c => c.ProcessId);
        var stored = (await db.Artifacts.AsNoTracking().Where(a => ids.Contains(a.ProcessId) && a.Type == ArtifactType.ORIGINAL_DOCUMENT)
            .Select(a => a.ProcessId).ToListAsync(ct)).ToHashSet();
        var docFailed = (await db.Operations.AsNoTracking().Where(o => ids.Contains(o.ProcessId)
                && (o.Type == OperationType.DOCUMENT_DOWNLOAD || o.Type == OperationType.DOCUMENT_STORE)
                && (o.Status == OperationStatus.FAILED || o.Status == OperationStatus.DLQ)).Select(o => o.ProcessId).ToListAsync(ct)).ToHashSet();
        var deliveries = (await db.CallbackDeliveries.AsNoTracking().Where(d => ids.Contains(d.ProcessId) && d.ProcessStatus == "COMPLETED")
            .Select(d => new { d.ProcessId, d.Status }).ToListAsync(ct)).ToLookup(d => d.ProcessId);

        var result = new Dictionary<string, ProgressDetailDto>();
        foreach (var (id, business, hasCallback) in procs)
        {
            var del = deliveries[id].Select(d => d.Status).ToList();
            var cb = del.Contains(DeliveryStatus.DELIVERED) ? CallbackState.Delivered
                : del.Any(s => s is DeliveryStatus.FAILED or DeliveryStatus.DLQ) ? CallbackState.Failed : CallbackState.Pending;
            result[id] = ProgressCalculator.Compute(new ProgressFacts(business, stored.Contains(id), docFailed.Contains(id),
                signers[id].Select(s => new SignerFact(s.Id, s.Name, s.Position, s.Order, s.Signed, s.ReleasedAt is not null)).ToList(),
                confs[id].Select(c => new ConfirmationFact(c.SignerId, c.Channel, c.Status)).ToList(), hasCallback, cb));
        }
        return result;
    }

    public async Task<PagedResult<ProcessListItem>> ListAsync(string? status, string? operationalStatus, string? q, int page, int pageSize, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        BusinessStatus? bs = null;
        OperationalStatus? os = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<BusinessStatus>(status, true, out var b)) bs = b; else errors.Add(new("status", "Unknown business status"));
        }
        if (!string.IsNullOrWhiteSpace(operationalStatus))
        {
            if (Enum.TryParse<OperationalStatus>(operationalStatus, true, out var o)) os = o; else errors.Add(new("operationalStatus", "Unknown operational status"));
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        (page, pageSize) = Norm(page, pageSize);

        var query = db.Processes.AsNoTracking().AsQueryable();
        if (caller?.ClientId is { } clientId) query = query.Where(p => p.ClientId == clientId);
        if (bs is not null) query = query.Where(p => p.BusinessStatus == bs);
        if (os is not null) query = query.Where(p => p.OperationalStatus == os);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(p => p.Id.ToLower().Contains(term) || p.ExternalId.ToLower().Contains(term));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new
            {
                p.Id, p.ExternalId, p.DocumentFileName, p.BusinessStatus, p.OperationalStatus, p.CreatedAt, p.UpdatedAt, HasCallback = p.CallbackJson != null,
                Provider = db.ProviderProcesses.Where(pp => pp.ProcessId == p.Id).Select(pp => pp.ProviderCode).FirstOrDefault(),
                Total = db.Signers.Count(s => s.ProcessId == p.Id),
                Signed = db.Signers.Count(s => s.ProcessId == p.Id && s.Signed)
            }).ToListAsync(ct);
        var now = clock.UtcNow;
        var progress = await ProgressAsync(rows.Select(r => (r.Id, r.BusinessStatus, r.HasCallback)).ToList(), ct);
        return new PagedResult<ProcessListItem>(rows.Select(r => new ProcessListItem(r.Id, r.ExternalId, r.DocumentFileName, r.Provider, r.Signed, r.Total,
            r.BusinessStatus.ToString(), r.OperationalStatus.ToString(), r.CreatedAt, r.UpdatedAt,
            SlaCalculator.For(r.BusinessStatus, r.OperationalStatus, r.CreatedAt, now, sla.Value.SigningMinutes),
            ProgressCalculator.Summary(progress[r.Id]))).ToList(), page, pageSize, total);
    }

    /// <summary>Provider metadata for operators. Reading it is itself audited (PRD section 36).</summary>
    public async Task<ProviderInfoDto> ProviderInfoAsync(string id, string correlationId, CancellationToken ct)
    {
        if (!await db.Processes.AnyAsync(p => p.Id == id, ct)) throw new NotFoundException("Process");
        var pp = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ProcessId == id, ct) ?? throw new NotFoundException("Provider registration");
        recorder.Journal(id, "PROVIDER_METADATA_INSPECTED", recorder.Api.Type, recorder.Api.Id, correlationId, null, new { provider = pp.ProviderCode });
        await db.SaveChangesAsync(ct);
        return new ProviderInfoDto(pp.ProviderCode, pp.ProviderProcessId, pp.ExternalReference, pp.NormalizedStatus,
            JsonDocument.Parse(pp.MetadataJson).RootElement.Clone(), pp.UpdatedAt);
    }

    public async Task<StatusDto> StatusAsync(string id, CancellationToken ct)
    {
        var p = await db.Processes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Process");
        return new StatusDto(p.Id, p.BusinessStatus.ToString(), p.OperationalStatus.ToString(), p.UpdatedAt);
    }

    public async Task<PagedResult<OperationDto>> OperationsAsync(string id, int page, int pageSize, CancellationToken ct)
    {
        await EnsureExists(id, ct);
        (page, pageSize) = Norm(page, pageSize);
        var q = db.Operations.AsNoTracking().Where(o => o.ProcessId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(o => o.CreatedAt).ThenBy(o => o.Sequence).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<OperationDto>(rows.Select(o => new OperationDto(o.Id, o.ProcessId, o.Type.ToString(), o.Status.ToString(),
            o.Attempt, o.MaxAttempts, o.NextRetryAt, Parse(o.OutputJson), Parse(o.ErrorJson), o.CreatedAt, o.UpdatedAt)).ToList(),
            page, pageSize, total);
    }

    public async Task<PagedResult<EventDto>> EventsAsync(string id, int page, int pageSize, CancellationToken ct)
    {
        await EnsureExists(id, ct);
        (page, pageSize) = Norm(page, pageSize);
        var q = db.Journal.AsNoTracking().Where(e => e.ProcessId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(e => e.Seq).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<EventDto>(rows.Select(e => new EventDto(e.Id, e.ProcessId, e.Type, e.OccurredAt,
            new { type = e.ActorType, id = e.ActorId }, Parse(e.MetadataJson) ?? default, e.CorrelationId, e.CausationId)).ToList(),
            page, pageSize, total);
    }

    private async Task EnsureExists(string id, CancellationToken ct)
    {
        if (!await db.Processes.AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Process");
    }

    private static (int, int) Norm(int page, int size) => (Math.Max(1, page), Math.Clamp(size <= 0 ? 50 : size, 1, MaxPageSize));

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}

public sealed class CancelProcessHandler(IOrchestratorDb db, IProviderAdapter provider, EventRecorder recorder, IClock clock, Callbacks.CallbackEmitter callbacks,
    ILogger<CancelProcessHandler> log)
{
    public async Task<ProcessSummary> HandleAsync(string id, string correlationId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var p = await db.Processes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Process");
            if (p.BusinessStatus == BusinessStatus.CANCELLED) return Summary(p);
            if (p.IsTerminal) throw new InvalidTransitionException("business", p.BusinessStatus.ToString(), "CANCELLED");

            var now = clock.UtcNow;
            try
            {
                p.TransitionBusiness(BusinessStatus.CANCELLED, now);
                if (p.OperationalStatus != OperationalStatus.READY) p.OperationalStatus = OperationalStatus.READY;
                var pending = await db.Operations.Where(o => o.ProcessId == id && o.Type != OperationType.CALLBACK_SEND && (o.Status == OperationStatus.NOT_STARTED || o.Status == OperationStatus.RETRY_PENDING)).ToListAsync(ct);
                foreach (var o in pending) { o.Status = OperationStatus.CANCELLED; o.UpdatedAt = now; }
                var cancelEvent = recorder.Journal(id, "PROCESS_CANCELLED", recorder.Api.Type, recorder.Api.Id, correlationId, null, new { cancelledOperations = pending.Count });
                await callbacks.EmitAsync(p, BusinessStatus.CANCELLED, correlationId, cancelEvent.Id, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5) { continue; }

            try { await provider.CancelAsync(id, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Provider cancel failed for process {ProcessId} (process stays cancelled)", id); }
            return Summary(p);
        }
    }

    private static ProcessSummary Summary(SignatureProcess p) =>
        new(p.Id, p.ExternalId, p.BusinessStatus.ToString(), p.OperationalStatus.ToString(), p.CreatedAt);
}
