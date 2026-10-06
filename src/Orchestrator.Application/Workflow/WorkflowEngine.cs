using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Workflow;

public enum WorkflowOutcome { Executed, Duplicate, Ignored, Failed, RetryScheduled, DeadLettered }

/// <summary>
/// Executes one operation of a process. Everything the execution changes (operation, process states,
/// journal, next operation, outbox, inbox marker) is committed in a single SaveChanges, so the effect and the
/// inbox record are atomic and duplicate deliveries are discarded.
/// </summary>
public sealed class WorkflowEngine(
    IOrchestratorDb db, IProviderAdapter provider, EventRecorder recorder, IClock clock,
    IOptions<WorkflowOptions> options, ArtifactService artifacts, IDocumentFetcher fetcher,
    IOptions<ArtifactOptions> artifactOptions, RetryPolicy retry, CallbackEmitter callbacks, CallbackDispatcher dispatcher, IdentityValidationRunner identity,
    Confirmation.ConfirmationService confirmations, Confirmation.SignerAdvancer advancer, ILogger<WorkflowEngine> log)
{
    public const string Consumer = "provider-worker";
    private const string Actor = "WORKER";

    public async Task<WorkflowOutcome> ExecuteAsync(string messageId, string operationId, string correlationId,
        string? causationId, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("operation.execute");
        activity?.SetTag("correlation.id", correlationId);
        activity?.SetTag("operation.id", operationId);
        activity?.SetTag("provider.id", provider.Code);
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                return await ExecuteOnceAsync(messageId, operationId, correlationId, causationId, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                log.LogInformation("Concurrent update on operation {OperationId}; retrying", operationId);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                if (await db.Inbox.AnyAsync(i => i.Consumer == Consumer && i.MessageId == messageId, ct))
                    return WorkflowOutcome.Duplicate;
                throw;
            }
        }
    }

    private async Task<WorkflowOutcome> ExecuteOnceAsync(string messageId, string operationId, string correlationId,
        string? causationId, CancellationToken ct)
    {
        var opType = await db.Operations.AsNoTracking().Where(o => o.Id == operationId).Select(o => (OperationType?)o.Type).FirstOrDefaultAsync(ct);
        System.Diagnostics.Activity.Current?.SetTag("operation.type", opType?.ToString());
        if (opType == OperationType.IDENTITY_VALIDATION)
            return await ExecuteIdentityAsync(messageId, operationId, correlationId, causationId, ct);

        if (await db.Inbox.AnyAsync(i => i.Consumer == Consumer && i.MessageId == messageId, ct))
            return WorkflowOutcome.Duplicate;

        var now = clock.UtcNow;
        db.Inbox.Add(new InboxMessage { Consumer = Consumer, MessageId = messageId, ReceivedAt = now });

        var op = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op is null)
        {
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.Ignored;
        }

        System.Diagnostics.Activity.Current?.SetTag("process.id", op.ProcessId);
        var p = await db.Processes.Include(x => x.Signers).FirstAsync(x => x.Id == op.ProcessId, ct);

        // Only pending work executes; anything else is a stale or duplicate delivery.
        if (op.Status is not (OperationStatus.NOT_STARTED or OperationStatus.RETRY_PENDING))
        {
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.Ignored;
        }

        if (p.IsTerminal && op.Type != OperationType.CALLBACK_SEND) // callbacks are independent of the process state
        {
            op.Status = OperationStatus.CANCELLED;
            op.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.Ignored;
        }

        op.Attempt++;
        op.NextRetryAt = null;
        op.UpdatedAt = now;
        if (!OperationDomains.IsAuxiliary(op.Type)) p.TransitionOperational(OperationalStatus.PROCESSING, now);

        Exception? failure = null;
        try
        {
            await RunAsync(p, op, correlationId, causationId, ct);
        }
        catch (Exception ex) when (!ErrorClassifier.IsInfrastructure(ex) && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            failure = ex;
        }

        if (failure is null)
        {
            op.Status = OperationStatus.COMPLETED;
            op.ErrorClass = null;
            op.UpdatedAt = clock.UtcNow;
            if (!OperationDomains.IsAuxiliary(op.Type) && p.OperationalStatus == OperationalStatus.PROCESSING) p.TransitionOperational(OperationalStatus.READY, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.Executed;
        }

        log.LogWarning(failure, "Operation {OperationId} ({Type}) failed on attempt {Attempt}", op.Id, op.Type, op.Attempt);
        return await HandleFailureAsync(messageId, operationId, correlationId, causationId, failure, ct);
    }

    /// <summary>
    /// Identity validations of one session are serialized: the session row is locked (SELECT ... FOR UPDATE) inside an
    /// explicit transaction, so concurrent validations can never miss each other results when closing the session.
    /// </summary>
    private async Task<WorkflowOutcome> ExecuteIdentityAsync(string messageId, string operationId, string correlationId,
        string? causationId, CancellationToken ct)
    {
        var sessionId = await db.Operations.AsNoTracking().Where(o => o.Id == operationId).Select(o => o.ProcessId).FirstAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM proofing_session WHERE id = {0} FOR UPDATE", [sessionId], ct);
        db.ChangeTracker.Clear();

        if (await db.Inbox.AnyAsync(i => i.Consumer == Consumer && i.MessageId == messageId, ct))
            return WorkflowOutcome.Duplicate;

        var now = clock.UtcNow;
        db.Inbox.Add(new InboxMessage { Consumer = Consumer, MessageId = messageId, ReceivedAt = now });
        var op = await db.Operations.FirstAsync(o => o.Id == operationId, ct);

        if (op.Status is not (OperationStatus.NOT_STARTED or OperationStatus.RETRY_PENDING))
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return WorkflowOutcome.Ignored;
        }

        op.Attempt++;
        op.NextRetryAt = null;
        op.UpdatedAt = now;

        Exception? failure = null;
        try
        {
            await identity.RunAsync(op, correlationId, causationId, ct);
        }
        catch (Exception ex) when (!ErrorClassifier.IsInfrastructure(ex) && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            failure = ex;
        }

        WorkflowOutcome outcome;
        if (failure is null)
        {
            op.Status = OperationStatus.COMPLETED;
            op.ErrorClass = null;
            op.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            outcome = WorkflowOutcome.Executed;
        }
        else
        {
            log.LogWarning(failure, "Identity validation {OperationId} failed on attempt {Attempt}", op.Id, op.Attempt);
            outcome = await HandleFailureAsync(messageId, operationId, correlationId, causationId, failure, ct);
        }
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Classifies the failure and decides: PERMANENT -> FAILED + MANUAL_ACTION; TRANSIENT/UNKNOWN with attempts left ->
    /// RETRY_PENDING (rescheduled through the outbox); otherwise -> DLQ of the domain of the operation.
    /// </summary>
    private async Task<WorkflowOutcome> HandleFailureAsync(string messageId, string operationId, string correlationId,
        string? causationId, Exception ex, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); // discard partial effects of the failed handler
        var now = clock.UtcNow;
        db.Inbox.Add(new InboxMessage { Consumer = Consumer, MessageId = messageId, ReceivedAt = now });
        var op = await db.Operations.FirstAsync(o => o.Id == operationId, ct);
        var p = await db.Processes.FirstOrDefaultAsync(x => x.Id == op.ProcessId, ct); // null for proofing sessions

        var cls = ErrorClassifier.Classify(ex);
        var reason = Truncate(ex.Message, 500);
        op.Attempt++;
        op.ErrorClass = cls;
        op.ErrorJson = JsonSerializer.Serialize(new { message = reason, type = ex.GetType().Name, errorClass = cls.ToString() });
        op.UpdatedAt = now;

        var max = retry.MaxAttemptsFor(op.Type);
        if (cls == ErrorClass.UNKNOWN) max = Math.Min(max, retry.UnknownMaxAttempts);

        void SetOperational(OperationalStatus to)
        {
            if (p is null || p.IsTerminal || OperationDomains.IsAuxiliary(op.Type)) return; // callback failures never change the process state
            if (p.OperationalStatus == OperationalStatus.READY) p.TransitionOperational(OperationalStatus.PROCESSING, now);
            p.TransitionOperational(to, now);
        }

        async Task SetDelivery(DeliveryStatus status)
        {
            if (op.Type != OperationType.CALLBACK_SEND) return;
            var d = await db.CallbackDeliveries.FirstOrDefaultAsync(x => x.OperationId == op.Id, ct);
            if (d is null) return;
            d.Status = status;
            d.Attempts = op.Attempt;
            d.LastError = reason;
            d.LastStatusCode = (ex as CallbackDeliveryException)?.StatusCode;
        }

        async Task SetValidation(ValidationStatus status)
        {
            if (op.Type != OperationType.IDENTITY_VALIDATION) return;
            var v = await db.IdentityValidations.FirstOrDefaultAsync(x => x.OperationId == op.Id, ct);
            if (v is null) return;
            v.Status = status;
            v.UpdatedAt = now;
        }

        if (op.Type == OperationType.CALLBACK_SEND) Telemetry.CallbackDeliveries.Add(1, new KeyValuePair<string, object?>("result", "failed"));
        if (cls == ErrorClass.PERMANENT)
        {
            op.Status = OperationStatus.FAILED;
            SetOperational(OperationalStatus.MANUAL_ACTION);
            await SetDelivery(DeliveryStatus.FAILED);
            await SetValidation(ValidationStatus.ERROR);
            recorder.Journal(op.ProcessId, "OPERATION_FAILED", "SYSTEM", Actor, correlationId, causationId,
                new { operationId = op.Id, type = op.Type.ToString(), errorClass = cls.ToString(), error = reason });
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.Failed;
        }

        if (op.Attempt < max)
        {
            var delay = retry.NextDelay(op.Type, op.Attempt);
            op.Status = OperationStatus.RETRY_PENDING;
            op.NextRetryAt = now + delay;
            SetOperational(OperationalStatus.RETRY_PENDING);
            await SetDelivery(DeliveryStatus.RETRY_PENDING);
            await SetValidation(ValidationStatus.PENDING);
            var ev = recorder.Journal(op.ProcessId, "OPERATION_RETRY_SCHEDULED", "SYSTEM", Actor, correlationId, causationId,
                new
                {
                    operationId = op.Id, type = op.Type.ToString(), attempt = op.Attempt, maxAttempts = max,
                    nextRetryAt = op.NextRetryAt, errorClass = cls.ToString(), error = reason
                });
            recorder.EnqueueOperation(op, correlationId, ev.Id, op.NextRetryAt.Value);
            Telemetry.OperationRetries.Add(1, new KeyValuePair<string, object?>("operation.type", op.Type.ToString()));
            await db.SaveChangesAsync(ct);
            return WorkflowOutcome.RetryScheduled;
        }

        var domain = OperationDomains.DomainOf(op.Type);
        op.Status = OperationStatus.DLQ;
        SetOperational(OperationalStatus.DLQ);
        await SetDelivery(DeliveryStatus.DLQ);
        await SetValidation(ValidationStatus.ERROR);
        var entry = new DeadLetterEntry
        {
            Id = Ids.New("dlq"), ProcessId = op.ProcessId, OperationId = op.Id, OperationType = op.Type.ToString(), Domain = domain,
            Queue = OperationDomains.DlqFor(domain), ErrorClass = cls, Reason = reason, Attempts = op.Attempt, CreatedAt = now
        };
        db.DeadLetters.Add(entry);
        var dlqEvent = recorder.Journal(op.ProcessId, "OPERATION_DEAD_LETTERED", "SYSTEM", Actor, correlationId, causationId,
            new
            {
                operationId = op.Id, type = op.Type.ToString(), deadLetterId = entry.Id, domain, attempts = op.Attempt,
                errorClass = cls.ToString()
            });
        recorder.EnqueueDeadLetter(entry, correlationId, dlqEvent.Id);
        await db.SaveChangesAsync(ct);
        return WorkflowOutcome.DeadLettered;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task<string> RunAsync(SignatureProcess p, Operation op, string corr, string? cause, CancellationToken ct)
    {
        string last = cause ?? "";
        JournalEvent J(string type, string actorType = "SYSTEM", string actorId = Actor, object? meta = null)
            => recorder.Journal(op.ProcessId, type, actorType, actorId, corr, last == "" ? null : last, meta);
        void Move(BusinessStatus to) => p.TransitionBusiness(to, clock.UtcNow);
        Task Notify(BusinessStatus s) => callbacks.EmitAsync(p, s, corr, last == "" ? null : last, ct);
        async Task Next(OperationType type, TimeSpan? delay = null)
        {
            var seq = await db.Operations.CountAsync(o => o.ProcessId == p.Id && o.Type == type, ct);
            var now = clock.UtcNow;
            var next = CreateProcessHandler.NewOperation(p.Id, type, seq, now, retry.MaxAttemptsFor(type));
            if (delay is not null) next.NextRetryAt = now + delay;
            db.Operations.Add(next);
            recorder.EnqueueOperation(next, corr, last == "" ? null : last, now + (delay ?? TimeSpan.Zero));
        }

        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        var fileName = FileNameOf(p);

        switch (op.Type)
        {
            case OperationType.DOCUMENT_DOWNLOAD:
            {
                var fetched = SourceUploadIdOf(p) is { } uploadId
                    ? await artifacts.ReadUploadAsync(uploadId, ct)
                    : await fetcher.FetchAsync(new Uri(SourceUrlOf(p)), artifactOptions.Value.MaxDocumentBytes, ct);
                var original = await artifacts.StoreAsync(p.Id, ArtifactType.ORIGINAL_DOCUMENT, fetched.Content, fetched.ContentType, fileName, ct);
                op.OutputJson = JsonSerializer.Serialize(new { artifactId = original.Id, sha256 = original.Sha256, size = original.Size });
                Move(BusinessStatus.DOCUMENT_RECEIVED);
                last = J("DOCUMENT_RECEIVED", meta: new { fileName, size = original.Size }).Id;
                await Next(OperationType.DOCUMENT_STORE);
                break;
            }

            case OperationType.DOCUMENT_STORE:
            {
                var original = await artifacts.RequireStoredAsync(p.Id, ArtifactType.ORIGINAL_DOCUMENT, ct);
                last = J("DOCUMENT_STORED", meta: new { artifactId = original.Id, sha256 = original.Sha256, size = original.Size }).Id;
                Move(BusinessStatus.VALIDATING);
                if (p.IdentityValidationsJson is not null)
                    last = J("IDENTITY_VALIDATION_REQUESTED", meta: new { capabilities = JsonDocument.Parse(p.IdentityValidationsJson).RootElement }).Id;
                await Next(OperationType.PROVIDER_CREATE_PROCESS);
                break;
            }

            case OperationType.PROVIDER_CREATE_PROCESS:
            {
                var info = await provider.CreateProcessAsync(new CreateProviderProcessRequest(
                    p.Id, p.ExternalId, p.SignatureType,
                    p.Signers.OrderBy(s => s.Position).Select(s => new ProviderSigner(s.Position, s.Name, s.Email, s.Document, s.Order)).ToList(), op.Attempt,
                    Confirmation.SignerAdvancer.IsGated(p.Signers)), ct);
                op.OutputJson = JsonSerializer.Serialize(new { providerProcessId = info.ProviderProcessId });
                last = J("PROVIDER_SELECTED", "SYSTEM", Actor, new { provider = provider.Code }).Id;
                last = J("PROVIDER_REQUEST_SENT", "SYSTEM", Actor, new { provider = provider.Code }).Id;
                last = J("PROVIDER_ACCEPTED", "PROVIDER", provider.Code, new { providerProcessId = info.ProviderProcessId }).Id;
                Move(BusinessStatus.READY_FOR_SIGNATURE);
                await Next(OperationType.PROVIDER_SEND_DOCUMENT);
                break;
            }

            case OperationType.PROVIDER_SEND_DOCUMENT:
            {
                // Providers without per-signer release only get the document after every confirmation (see SignerAdvancer).
                var deferSend = Confirmation.SignerAdvancer.IsGated(p.Signers) && !await provider.SupportsPerSignerReleaseAsync(p.Id, ct);
                if (deferSend) last = J("PROVIDER_SEND_DEFERRED", meta: new { reason = "waiting for every signer confirmation" }).Id;
                else
                {
                    await provider.SendDocumentAsync(p.Id, fileName, ct);
                    last = J("SIGNER_NOTIFIED", "PROVIDER", provider.Code, new { signers = p.Signers.Count }).Id;
                    last = J("SIGNATURE_STARTED", "PROVIDER", provider.Code).Id;
                }
                Move(BusinessStatus.SIGNATURE_IN_PROGRESS);
                last = await advancer.AdvanceAsync(p, corr, last == "" ? null : last, ct) ?? last;
                await Notify(BusinessStatus.SIGNATURE_IN_PROGRESS);
                await Next(OperationType.PROVIDER_STATUS_CHECK, poll);
                break;
            }

            case OperationType.PROVIDER_STATUS_CHECK:
            {
                last = await advancer.AdvanceAsync(p, corr, last == "" ? null : last, ct) ?? last;
                var st = await provider.GetStatusAsync(p.Id, ct);
                var now = clock.UtcNow;
                foreach (var s in p.Signers.Where(s => !s.Signed && st.SignedPositions.Contains(s.Position)))
                {
                    s.Signed = true;
                    s.SignedAt = now;
                    last = J("SIGNER_SIGNED", "PROVIDER", provider.Code, new { signerId = s.Id, position = s.Position }).Id;
                }
                op.OutputJson = JsonSerializer.Serialize(new { status = st.NormalizedStatus, signed = p.Signers.Count(s => s.Signed) });
                switch (st.NormalizedStatus)
                {
                    case ProviderStatuses.Signed:
                        Move(BusinessStatus.SIGNED);
                        last = J("SIGNATURE_COMPLETED", "PROVIDER", provider.Code).Id;
                        await Notify(BusinessStatus.SIGNED);
                        await Next(OperationType.SIGNED_DOCUMENT_DOWNLOAD);
                        break;
                    case ProviderStatuses.Rejected:
                        Move(BusinessStatus.REJECTED);
                        last = J("SIGNATURE_REJECTED", "PROVIDER", provider.Code).Id;
                        await Notify(BusinessStatus.REJECTED);
                        break;
                    case ProviderStatuses.Cancelled:
                        Move(BusinessStatus.CANCELLED);
                        last = J("PROCESS_CANCELLED", "PROVIDER", provider.Code).Id;
                        await Notify(BusinessStatus.CANCELLED);
                        break;
                    default:
                        if (st.NormalizedStatus == ProviderStatuses.PartiallySigned
                            && p.BusinessStatus == BusinessStatus.SIGNATURE_IN_PROGRESS)
                        { Move(BusinessStatus.PARTIALLY_SIGNED); await Notify(BusinessStatus.PARTIALLY_SIGNED); }
                        last = await advancer.AdvanceAsync(p, corr, last == "" ? null : last, ct) ?? last;
                        await Next(OperationType.PROVIDER_STATUS_CHECK, poll);
                        break;
                }
                break;
            }

            case OperationType.SIGNED_DOCUMENT_DOWNLOAD:
            {
                var original = await artifacts.RequireStoredAsync(p.Id, ArtifactType.ORIGINAL_DOCUMENT, ct);
                var originalBytes = await artifacts.ReadAsync(original, ct);
                var signedFile = await provider.DownloadSignedDocumentAsync(p.Id, originalBytes, original.ContentType, ct);
                var evidenceFile = await provider.DownloadEvidenceAsync(p.Id, ct);
                var signed = await artifacts.StoreAsync(p.Id, ArtifactType.SIGNED_DOCUMENT, signedFile.Content, signedFile.ContentType, signedFile.FileName, ct);
                var evidence = await artifacts.StoreAsync(p.Id, ArtifactType.EVIDENCE, evidenceFile.Content, evidenceFile.ContentType, evidenceFile.FileName, ct);
                op.OutputJson = JsonSerializer.Serialize(new { signedArtifactId = signed.Id, evidenceArtifactId = evidence.Id });
                Move(BusinessStatus.FINALIZING);
                last = J("SIGNED_DOCUMENT_RECEIVED", "PROVIDER", provider.Code,
                    new { artifactId = signed.Id, sha256 = signed.Sha256, evidenceArtifactId = evidence.Id }).Id;
                await Next(OperationType.SIGNED_DOCUMENT_STORE);
                break;
            }

            case OperationType.SIGNED_DOCUMENT_STORE:
            {
                var signed = await artifacts.RequireStoredAsync(p.Id, ArtifactType.SIGNED_DOCUMENT, ct);
                var evidence = await artifacts.RequireStoredAsync(p.Id, ArtifactType.EVIDENCE, ct);
                last = J("FINAL_DOCUMENT_STORED", meta: new
                {
                    artifactId = signed.Id, sha256 = signed.Sha256, size = signed.Size, evidenceArtifactId = evidence.Id
                }).Id;
                Move(BusinessStatus.COMPLETED);
                last = J("PROCESS_COMPLETED").Id;
                await Notify(BusinessStatus.COMPLETED);
                break;
            }

            case OperationType.CONFIRMATION_SEND:
                last = await confirmations.SendAsync(op, corr, last == "" ? null : last, ct) ?? last;
                break;

            case OperationType.CALLBACK_SEND:
                last = await dispatcher.DispatchAsync(p, op, corr, last == "" ? null : last, ct);
                break;

            default:
                throw new InvalidOperationException($"Operation type {op.Type} has no handler in this workflow");
        }
        return last;
    }

    private static string? SourceUploadIdOf(SignatureProcess p)
    {
        using var doc = JsonDocument.Parse(p.RequestJson);
        var src = doc.RootElement.GetProperty("document").GetProperty("source");
        return src.TryGetProperty("type", out var t) && t.GetString() == "UPLOAD" ? src.GetProperty("uploadId").GetString() : null;
    }

    private static string SourceUrlOf(SignatureProcess p)
    {
        using var doc = JsonDocument.Parse(p.RequestJson);
        return doc.RootElement.GetProperty("document").GetProperty("source").GetProperty("url").GetString()!;
    }

    private static string FileNameOf(SignatureProcess p)
    {
        using var doc = JsonDocument.Parse(p.RequestJson);
        return doc.RootElement.GetProperty("document").GetProperty("fileName").GetString() ?? "document.pdf";
    }
}
