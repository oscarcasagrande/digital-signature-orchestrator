using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Identity;

public sealed record ProofingSummary(string SessionId, string ExternalId, string Status, string Result, DateTime CreatedAt);

/// <summary>Shared rules of a proofing session: which validations can start and when the session is complete.</summary>
public sealed class ProofingCore(IOrchestratorDb db, EventRecorder recorder, IClock clock, RetryPolicy retry)
{
    public const string Actor = "proofing";

    public async Task<IReadOnlySet<ArtifactType>> EvidenceAsync(string sessionId, CancellationToken ct)
    {
        var persisted = await db.Artifacts.AsNoTracking().Where(a => a.ProcessId == sessionId).Select(a => a.Type).ToListAsync(ct);
        var added = db.ChangeTracker.Entries<Artifact>()
            .Where(e => e.State == EntityState.Added && e.Entity.ProcessId == sessionId).Select(e => e.Entity.Type);
        return persisted.Concat(added).ToHashSet();
    }

    /// <summary>Creates one IDENTITY_VALIDATION operation per validation whose prerequisites are now met (call under the session lock).</summary>
    public async Task CreateReadyOperationsAsync(ProofingSession session, string correlationId, string? causationId, CancellationToken ct)
    {
        var evidence = await EvidenceAsync(session.Id, ct);
        var persisted = await db.Operations.CountAsync(o => o.ProcessId == session.Id && o.Type == OperationType.IDENTITY_VALIDATION, ct);
        var pending = db.ChangeTracker.Entries<Operation>().Count(e =>
            e.State == EntityState.Added && e.Entity.ProcessId == session.Id && e.Entity.Type == OperationType.IDENTITY_VALIDATION);
        var seq = persisted + pending;
        var now = clock.UtcNow;

        foreach (var v in session.Validations.OrderBy(v => v.Capability == IdentityCapabilities.IdentityRisk ? 1 : 0).ThenBy(v => v.Capability).ToList())
        {
            if (v.Status != ValidationStatus.WAITING_EVIDENCE || !IdentityCapabilities.IsReady(v.Capability, evidence, session.Validations)) continue;
            var op = CreateProcessHandler.NewOperation(session.Id, OperationType.IDENTITY_VALIDATION, seq++, now,
                retry.MaxAttemptsFor(OperationType.IDENTITY_VALIDATION));
            op.InputJson = JsonSerializer.Serialize(new { validationId = v.Id });
            v.Status = ValidationStatus.PENDING;
            v.OperationId = op.Id;
            v.UpdatedAt = now;
            db.Operations.Add(op);
            var ev = recorder.Journal(session.Id, "IDENTITY_VALIDATION_REQUESTED", "SYSTEM", Actor, correlationId, causationId,
                new { validationId = v.Id, capability = v.Capability, operationId = op.Id });
            recorder.EnqueueOperation(op, correlationId, ev.Id, now);
        }
    }

    /// <summary>Updates the session state and, when every validation finished, closes it with the aggregated result.</summary>
    public void Recompute(ProofingSession s, string correlationId, string? causationId)
    {
        var now = clock.UtcNow;
        var vs = s.Validations;
        if (vs.Count > 0 && vs.All(v => v.IsTerminal))
        {
            if (s.Status != ProofingSessionStatus.COMPLETED)
            {
                s.Status = ProofingSessionStatus.COMPLETED;
                s.CompletedAt = now;
                s.Result = vs.Any(v => v.Required && v.Status == ValidationStatus.FAILED) ? ProofingResult.REJECTED : ProofingResult.APPROVED;
                recorder.Journal(s.Id, "PROOFING_SESSION_COMPLETED", "SYSTEM", Actor, correlationId, causationId, new { result = s.Result.ToString() });
            }
        }
        else
        {
            s.Status = vs.Any(v => v.Status == ValidationStatus.WAITING_EVIDENCE && v.Capability != IdentityCapabilities.IdentityRisk)
                ? ProofingSessionStatus.WAITING_EVIDENCE : ProofingSessionStatus.IN_PROGRESS;
            s.Result = ProofingResult.PENDING;
        }
        s.UpdatedAt = now;
    }

    /// <summary>Starts a transaction holding the session row lock; everything that changes a session runs under it.</summary>
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> LockSessionAsync(IOrchestratorDb db, string sessionId, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM proofing_session WHERE id = {0} FOR UPDATE", [sessionId], ct);
        db.ChangeTracker.Clear();
        return tx;
    }
}

public sealed class ProofingService(IOrchestratorDb db, ProofingCore core, ArtifactService artifacts, EventRecorder recorder, IClock clock,
    IOptions<IdempotencyOptions> idempotency, IOptions<IdentityOptions> options, CallerContext? caller = null)
{
    private static readonly string[] ContentTypes = ["image/jpeg", "image/png"];

    public async Task<(ProofingSummary Session, bool Created)> CreateAsync(string? idempotencyKey, string body, string correlationId, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(idempotencyKey)) errors.Add(new("Idempotency-Key", "Header is required"));
        else if (idempotencyKey.Length > 128) errors.Add(new("Idempotency-Key", "Max length is 128"));

        JsonNode? root = null;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { errors.Add(new("body", "Invalid JSON")); }
        ProofingRequest? request = null;
        if (!errors.Any(e => e.Field == "body"))
        {
            var (req, parseErrors) = ProofingRequestValidator.Parse(root);
            request = req;
            errors.AddRange(parseErrors);
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        // Idempotency keys are per client.
        var key = "proofing:" + (caller?.ClientId is { } cid ? cid + ":" : "") + idempotencyKey;
        var hash = CreateProcessValidator.Hash(root);
        var now = clock.UtcNow;

        var existing = await db.Idempotency.AsNoTracking().FirstOrDefaultAsync(i => i.Key == key, ct);
        if (existing is not null)
        {
            if (now - existing.CreatedAt > TimeSpan.FromHours(idempotency.Value.RetentionHours))
                await db.Idempotency.Where(i => i.Key == key && i.CreatedAt == existing.CreatedAt).ExecuteDeleteAsync(ct);
            else
                return (await ReplayAsync(existing.RequestHash, existing.ProcessId, hash, ct), false);
        }

        var session = new ProofingSession
        {
            Id = Ids.New("prf"), ExternalId = request!.ExternalId, CorrelationId = correlationId, CreatedAt = now, UpdatedAt = now,
            ClientId = caller?.ClientId,
            SubjectJson = JsonSerializer.Serialize(new { name = request.Subject.Name, document = request.Subject.Document,
                phone = request.Subject.Phone, email = request.Subject.Email, deviceId = request.Subject.DeviceId })
        };
        foreach (var v in request.Validations)
            session.Validations.Add(new IdentityValidation
            {
                Id = Ids.New("ivl"), SessionId = session.Id, Capability = v.Capability, Required = v.Required, CreatedAt = now, UpdatedAt = now
            });
        db.ProofingSessions.Add(session);
        db.Idempotency.Add(new IdempotencyRecord { Key = key, RequestHash = hash, ProcessId = session.Id, CreatedAt = now });
        var ev = recorder.Journal(session.Id, "PROOFING_SESSION_CREATED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
            new { externalId = session.ExternalId, validations = request.Validations.Select(v => new { type = v.Capability, required = v.Required }) });
        await core.CreateReadyOperationsAsync(session, correlationId, ev.Id, ct);
        core.Recompute(session, correlationId, ev.Id);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var winner = await db.Idempotency.AsNoTracking().FirstAsync(i => i.Key == key, ct);
            return (await ReplayAsync(winner.RequestHash, winner.ProcessId, hash, ct), false);
        }
        return (Summary(session), true);
    }

    private async Task<ProofingSummary> ReplayAsync(string storedHash, string sessionId, string hash, CancellationToken ct)
    {
        if (storedHash != hash) throw new IdempotencyConflictException();
        return Summary(await db.ProofingSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct));
    }

    private static ProofingSummary Summary(ProofingSession s) =>
        new(s.Id, s.ExternalId, s.Status.ToString(), s.Result.ToString(), s.CreatedAt);

    public sealed record EvidenceReceipt(string SessionId, string Type, string Sha256, long Size, string ContentType, string SessionStatus);

    /// <param name="allowedTypes">Evidence types accepted by the endpoint (documents vs biometrics).</param>
    public async Task<EvidenceReceipt> ReceiveEvidenceAsync(string sessionId, ArtifactType[] allowedTypes, string body, string correlationId, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        JsonNode? root = null;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { errors.Add(new("body", "Invalid JSON")); }
        string? typeText = null, contentType = null, content = null;
        if (root is JsonObject o)
        {
            typeText = (o["type"] as JsonValue)?.GetValue<string>();
            contentType = (o["contentType"] as JsonValue)?.GetValue<string>();
            content = (o["content"] as JsonValue)?.GetValue<string>();
        }
        else if (errors.Count == 0) errors.Add(new("body", "Body must be a JSON object"));

        ArtifactType type = default;
        if (typeText is null || !Enum.TryParse(typeText, out type) || !allowedTypes.Contains(type))
            errors.Add(new("type", $"Must be one of {string.Join(", ", allowedTypes)}"));
        if (contentType is null || !ContentTypes.Contains(contentType)) errors.Add(new("contentType", $"Must be one of {string.Join(", ", ContentTypes)}"));
        byte[] bytes = [];
        if (string.IsNullOrEmpty(content)) errors.Add(new("content", "Required (base64)"));
        else
        {
            try { bytes = Convert.FromBase64String(content); }
            catch (FormatException) { errors.Add(new("content", "Invalid base64")); }
            if (bytes.Length == 0 && errors.All(e => e.Field != "content")) errors.Add(new("content", "Empty content"));
            if (bytes.LongLength > options.Value.MaxEvidenceBytes) errors.Add(new("content", $"Exceeds the {options.Value.MaxEvidenceBytes} byte limit"));
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        await using var tx = await ProofingCore.LockSessionAsync(db, sessionId, ct);
        var session = await db.ProofingSessions.Include(s => s.Validations).FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                      ?? throw new NotFoundException("Proofing session");
        if (session.Status == ProofingSessionStatus.COMPLETED) throw new ConflictException("The session is already completed");
        if (await db.Artifacts.AnyAsync(a => a.ProcessId == sessionId && a.Type == type, ct))
            throw new ConflictException($"Evidence {type} was already provided");

        var artifact = await artifacts.StoreAsync(sessionId, type, bytes, contentType!, type.ToString().ToLowerInvariant(), ct);
        var ev = recorder.Journal(sessionId, "EVIDENCE_RECEIVED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
            new { type = type.ToString(), sha256 = artifact.Sha256, size = artifact.Size, contentType });
        await core.CreateReadyOperationsAsync(session, correlationId, ev.Id, ct);
        core.Recompute(session, correlationId, ev.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new EvidenceReceipt(sessionId, type.ToString(), artifact.Sha256, artifact.Size, contentType!, session.Status.ToString());
    }
}

/// <summary>Executes one IDENTITY_VALIDATION operation (called by the engine under the session lock).</summary>
public sealed class IdentityValidationRunner(IOrchestratorDb db, IIdentityProofingAdapter adapter, ArtifactService artifacts,
    ProofingCore core, EventRecorder recorder, IClock clock)
{
    public async Task<string?> RunAsync(Operation op, string correlationId, string? causationId, CancellationToken ct)
    {
        using var input = JsonDocument.Parse(op.InputJson ?? "{}");
        var validationId = input.RootElement.GetProperty("validationId").GetString()!;
        var v = await db.IdentityValidations.FirstOrDefaultAsync(x => x.Id == validationId, ct)
                ?? throw new OperationException("Validation not found", transient: false);
        var session = await db.ProofingSessions.Include(s => s.Validations).FirstAsync(s => s.Id == v.SessionId, ct);
        if (v.IsTerminal) return causationId; // already evaluated: idempotent no-op

        using var subjectDoc = JsonDocument.Parse(session.SubjectJson);
        var sj = subjectDoc.RootElement;
        string? S(string name) => sj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var subject = new ProofingSubject(S("name")!, S("document")!, S("phone"), S("email"), S("deviceId"));

        var evidence = new Dictionary<ArtifactType, byte[]>();
        foreach (var type in IdentityCapabilities.EvidenceFor(v.Capability))
        {
            var a = await artifacts.RequireStoredAsync(session.Id, type, ct);
            evidence[type] = await artifacts.ReadAsync(a, ct);
        }
        var prior = session.Validations.Where(o => o.Id != v.Id && o.IsTerminal)
            .Select(o => new PriorResult(o.Capability, o.Status == ValidationStatus.PASSED, o.Score)).ToList();

        var outcome = await adapter.ValidateAsync(new IdentityValidationRequest(session.Id, session.ExternalId, v.Capability, subject,
            evidence, prior, op.Attempt), ct);

        var now = clock.UtcNow;
        v.Status = outcome.Passed ? ValidationStatus.PASSED : ValidationStatus.FAILED;
        if (!outcome.Passed) Telemetry.ProofingFailures.Add(1, new KeyValuePair<string, object?>("capability", v.Capability.ToString()));
        v.Score = outcome.Score;
        v.DetailsJson = outcome.DetailsJson;
        v.ProviderCode = adapter.Code;
        v.CompletedAt = now;
        v.UpdatedAt = now;
        op.OutputJson = JsonSerializer.Serialize(new { status = v.Status.ToString() });
        var ev = recorder.Journal(session.Id, "IDENTITY_VALIDATED", "PROVIDER", adapter.Code, correlationId, causationId,
            new { validationId = v.Id, capability = v.Capability, status = v.Status.ToString(), score = v.Score });

        await core.CreateReadyOperationsAsync(session, correlationId, ev.Id, ct); // IDENTITY_RISK once everything else finished
        core.Recompute(session, correlationId, ev.Id);
        return ev.Id;
    }
}
