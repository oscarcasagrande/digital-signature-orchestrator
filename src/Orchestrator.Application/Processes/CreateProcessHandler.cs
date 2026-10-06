using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Security;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Processes;

public sealed class IdempotencyOptions
{
    public int RetentionHours { get; set; } = 24;
}

public sealed record ProcessSummary(
    string ProcessId, string ExternalId, string BusinessStatus, string OperationalStatus, DateTime CreatedAt);

public sealed record CreateProcessResult(ProcessSummary Process, bool Created);

public sealed class CreateProcessHandler(
    IOrchestratorDb db, EventRecorder recorder, IClock clock,
    IOptions<IdempotencyOptions> options, Resilience.RetryPolicy retry, Callbacks.SsrfGuard guard, Artifacts.DocumentUploadService uploads, ILogger<CreateProcessHandler> log,
    CallerContext? caller = null)
{
    public async Task<CreateProcessResult> HandleAsync(string? idempotencyKey, string body, string correlationId, CancellationToken ct)
    {
        var keyErrors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(idempotencyKey)) keyErrors.Add(new("Idempotency-Key", "Header is required"));
        else if (idempotencyKey.Length > 128) keyErrors.Add(new("Idempotency-Key", "Max length is 128"));

        JsonNode? root = null;
        try { root = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { keyErrors.Add(new("body", "Invalid JSON")); }

        var errors = keyErrors.Concat(root is null && keyErrors.Any(e => e.Field == "body")
            ? [] : CreateProcessValidator.Validate(root)).ToList();
        if (errors.Count > 0) throw new ValidationException(errors);

        // Idempotency keys are scoped to who calls: per client for machine clients, per user for people (operators/admins in the
        // portal), so the same key from two callers never collides or replays someone else's process.
        static string Scope(string prefix, string who, string key) =>
            prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(who + "|" + key)))[..40].ToLowerInvariant();
        if (caller?.ClientId is { } clientId) idempotencyKey = Scope("c:", clientId, idempotencyKey!);
        else if (caller is { Authenticated: true } person) idempotencyKey = Scope("u:", person.UserId, idempotencyKey!);

        var hash = CreateProcessValidator.Hash(root);
        var now = clock.UtcNow;

        var existing = await db.Idempotency.AsNoTracking().FirstOrDefaultAsync(i => i.Key == idempotencyKey, ct);
        if (existing is not null)
        {
            if (now - existing.CreatedAt > TimeSpan.FromHours(options.Value.RetentionHours))
                await db.Idempotency.Where(i => i.Key == idempotencyKey && i.CreatedAt == existing.CreatedAt).ExecuteDeleteAsync(ct);
            else
                return await ReplayAsync(existing.RequestHash, existing.ProcessId, hash, ct);
        }

        // Destination policy is checked after the idempotency replay so a replay still returns the original process.
        if (root is JsonObject rootObj && rootObj["callback"] is JsonObject cbObj)
        {
            var cbId = (cbObj["callbackId"] as JsonValue)?.GetValue<string>();
            var cbUrl = (cbObj["url"] as JsonValue)?.GetValue<string>();
            var cbErrors = new List<FieldError>();
            if (cbId is not null)
            {
                var reg = await db.CallbackRegistrations.AsNoTracking().FirstOrDefaultAsync(c => c.CallbackId == cbId, ct);
                var foreign = reg?.ClientId is { } owner && caller?.ClientId is { } me && owner != me;
                if (reg is null || !reg.Active || foreign) cbErrors.Add(new FieldError("callback.callbackId", "Unknown or inactive callbackId"));
            }
            else if (cbUrl is not null && guard.ValidateUrl(cbUrl) is { } urlError)
                cbErrors.Add(new FieldError("callback.url", urlError));
            if (cbErrors.Count > 0) throw new ValidationException(cbErrors);
        }

        if (root is JsonObject docRoot && docRoot["document"]?["source"] is JsonObject srcObj && (srcObj["type"] as JsonValue)?.GetValue<string>() == "UPLOAD")
            if (await uploads.CheckAsync(srcObj["uploadId"]!.GetValue<string>(), ct) is { } uploadError) throw new ValidationException([uploadError]);

        var obj = root!.AsObject();
        var (process, confirmations) = Build(obj, correlationId, now);
        process.ClientId = caller?.ClientId;
        var firstOp = NewOperation(process.Id, OperationType.DOCUMENT_DOWNLOAD, 0, now, retry.MaxAttemptsFor(OperationType.DOCUMENT_DOWNLOAD));
        db.Processes.Add(process);
        db.Confirmations.AddRange(confirmations);
        db.Operations.Add(firstOp);
        db.Idempotency.Add(new IdempotencyRecord { Key = idempotencyKey!, RequestHash = hash, ProcessId = process.Id, CreatedAt = now });
        var ev = recorder.Journal(process.Id, "PROCESS_CREATED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
            new { externalId = process.ExternalId, signers = process.Signers.Count });
        recorder.EnqueueOperation(firstOp, correlationId, ev.Id, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Concurrent request with the same key won the insert-first race.
            db.ChangeTracker.Clear();
            var winner = await db.Idempotency.AsNoTracking().FirstAsync(i => i.Key == idempotencyKey, ct);
            return await ReplayAsync(winner.RequestHash, winner.ProcessId, hash, ct);
        }

        log.LogInformation("Process {ProcessId} created (externalId={ExternalId}, signers={Signers})",
            process.Id, process.ExternalId, string.Join(",", process.Signers.Select(s => SensitiveMasker.MaskDocument(s.Document))));
        return new CreateProcessResult(Summary(process), true);
    }

    private async Task<CreateProcessResult> ReplayAsync(string storedHash, string processId, string hash, CancellationToken ct)
    {
        if (storedHash != hash) throw new IdempotencyConflictException();
        var p = await db.Processes.AsNoTracking().FirstAsync(x => x.Id == processId, ct);
        return new CreateProcessResult(Summary(p), false);
    }

    private static ProcessSummary Summary(SignatureProcess p) =>
        new(p.Id, p.ExternalId, p.BusinessStatus.ToString(), p.OperationalStatus.ToString(), p.CreatedAt);

    public static Operation NewOperation(string processId, OperationType type, int sequence, DateTime now, int maxAttempts) => new()
    {
        Id = Ids.New("op"), ProcessId = processId, Type = type, Sequence = sequence, MaxAttempts = maxAttempts,
        CreatedAt = now, UpdatedAt = now
    };

    private static (SignatureProcess, List<SignerConfirmation>) Build(JsonObject o, string correlationId, DateTime now)
    {
        var id = Ids.New("sig");
        var (signers, _) = SignerPlan.Resolve(o); // already validated by the caller
        var legacyType = (o["signature"]?["type"] as JsonValue)?.GetValue<string>()?.ToUpperInvariant();
        var p = new SignatureProcess
        {
            Id = id, ExternalId = o["externalId"]!.GetValue<string>(),
            SignatureType = legacyType is not null && SignerPlan.SignatureTypes.Contains(legacyType) ? legacyType : SignerPlan.HighestType(signers.Select(s => s.SignatureType)),
            RequestJson = CreateProcessValidator.Canonicalize(o),
            CallbackJson = o["callback"]?.ToJsonString(),
            IdentityValidationsJson = o["identityProofing"]?["validations"]?.ToJsonString(),
            CorrelationId = correlationId, CreatedAt = now, UpdatedAt = now,
            DocumentFileName = o["document"]?["fileName"]?.GetValue<string>() is { } fn && fn.Length > 300 ? fn[..300] : o["document"]?["fileName"]?.GetValue<string>()
        };
        var confirmations = new List<SignerConfirmation>();
        foreach (var s in signers)
        {
            var signer = new Signer
            {
                Id = Ids.New("sgn"), ProcessId = id, Position = s.Position, ExternalId = s.ExternalId, Name = s.Name, Document = s.Document,
                Email = s.Email, Phone = s.Phone, SignatureType = s.SignatureType, Order = s.Order,
                ConfirmationChannels = string.Join(',', s.Channels)
            };
            p.Signers.Add(signer);
            confirmations.AddRange(s.Channels.Select(c => new SignerConfirmation
            {
                Id = Ids.New("cnf"), ProcessId = id, SignerId = signer.Id, Channel = c, CreatedAt = now
            }));
        }
        return (p, confirmations);
    }
}

/// <summary>Workflow knobs shared by create and engine (bound from configuration section "Workflow").</summary>
public sealed class WorkflowOptions
{
    public int PollIntervalSeconds { get; set; } = 2;
}
