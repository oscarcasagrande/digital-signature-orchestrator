using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Workflow;

public sealed record ReprocessRequest(string? OperationId, string? Reason);

public sealed record ReprocessResult(string ProcessId, string OperationId, string OperationType, string OperationalStatus);

/// <summary>
/// Resumes a process from the operation that failed (or the one indicated) without restarting it: only that
/// operation is put back to NOT_STARTED with a fresh attempt budget; completed operations are never re-run.
/// </summary>
public sealed class ReprocessHandler(IOrchestratorDb db, EventRecorder recorder, IClock clock)
{
    private static readonly OperationStatus[] Reprocessable =
        [OperationStatus.FAILED, OperationStatus.DLQ, OperationStatus.RETRY_PENDING];

    public async Task<ReprocessResult> HandleAsync(string processId, ReprocessRequest? request, string correlationId, CancellationToken ct)
    {
        if (request?.Reason is { Length: > 500 })
            throw new ValidationException([new FieldError("reason", "Max length is 500")]);

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var p = await db.Processes.FirstOrDefaultAsync(x => x.Id == processId, ct) ?? throw new NotFoundException("Process");
            Domain.Entities.Operation op;
            if (!string.IsNullOrWhiteSpace(request?.OperationId))
            {
                op = await db.Operations.FirstOrDefaultAsync(o => o.Id == request.OperationId && o.ProcessId == processId, ct)
                     ?? throw new NotFoundException("Operation");
                if (!Reprocessable.Contains(op.Status))
                    throw new ConflictException($"Operation is {op.Status} and cannot be reprocessed");
            }
            else
            {
                if (p.IsTerminal) throw new ConflictException($"Process is {p.BusinessStatus} and cannot be reprocessed");
                // Callback deliveries are independent of the main flow, so they are only reprocessed when named explicitly.
                op = (await db.Operations.Where(o => o.ProcessId == processId && o.Type != OperationType.CALLBACK_SEND && Reprocessable.Contains(o.Status))
                         .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Sequence).FirstOrDefaultAsync(ct))
                     ?? throw new ConflictException("The process has no operation to reprocess");
            }
            var isCallback = op.Type == OperationType.CALLBACK_SEND;
            if (p.IsTerminal && !isCallback) throw new ConflictException($"Process is {p.BusinessStatus} and cannot be reprocessed");

            var now = clock.UtcNow;
            var previous = op.Status;
            op.Status = OperationStatus.NOT_STARTED;
            op.Attempt = 0;
            op.ErrorJson = null;
            op.ErrorClass = null;
            op.NextRetryAt = null;
            op.UpdatedAt = now;
            if (!isCallback) p.TransitionOperational(OperationalStatus.READY, now); // callbacks never touch the process state
            p.UpdatedAt = now; // always modifies the process so concurrent reprocess requests conflict on its version
            if (isCallback)
            {
                var delivery = await db.CallbackDeliveries.FirstOrDefaultAsync(d => d.OperationId == op.Id, ct);
                if (delivery is not null) { delivery.Status = DeliveryStatus.PENDING; delivery.LastError = null; }
            }

            foreach (var dl in await db.DeadLetters.Where(d => d.OperationId == op.Id && d.ResolvedAt == null).ToListAsync(ct))
            {
                dl.ResolvedAt = now;
                dl.ResolvedBy = "api";
            }

            var ev = recorder.Journal(processId, "OPERATION_REPROCESS_REQUESTED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
                new { operationId = op.Id, type = op.Type.ToString(), previousStatus = previous.ToString(), reason = request?.Reason });
            recorder.EnqueueOperation(op, correlationId, ev.Id, now);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5) { continue; }
            return new ReprocessResult(processId, op.Id, op.Type.ToString(), p.OperationalStatus.ToString());
        }
    }
}
