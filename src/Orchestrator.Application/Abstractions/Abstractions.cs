using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Orchestrator.Domain.Entities;
using Orchestrator.Application;

namespace Orchestrator.Application.Abstractions;

public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

public static class Ids
{
    private static long _counter;

    /// <summary>
    /// Time-ordered id (unix ms + process-wide counter + random suffix). Ids created later sort later, so rows
    /// created in one unit of work are inserted (and receive journal sequence numbers) in creation order.
    /// </summary>
    public static string New(string prefix)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var n = Interlocked.Increment(ref _counter) & 0xFFFF;
        return $"{prefix}_{ms:x12}{n:x4}{Guid.NewGuid().ToString("N")[..8]}";
    }
}

public interface IOrchestratorDb
{
    DbSet<SignatureProcess> Processes { get; }
    DbSet<Signer> Signers { get; }
    DbSet<Operation> Operations { get; }
    DbSet<JournalEvent> Journal { get; }
    DbSet<OutboxEvent> Outbox { get; }
    DbSet<InboxMessage> Inbox { get; }
    DbSet<IdempotencyRecord> Idempotency { get; }
    DbSet<ProviderProcess> ProviderProcesses { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<DeadLetterEntry> DeadLetters { get; }
    DbSet<CallbackRegistration> CallbackRegistrations { get; }
    DbSet<CallbackDelivery> CallbackDeliveries { get; }
    DbSet<ProofingSession> ProofingSessions { get; }
    DbSet<IdentityValidation> IdentityValidations { get; }
    DbSet<ReconciliationRecord> ReconciliationRecords { get; }
    DbSet<SignerConfirmation> Confirmations { get; }
    DbSet<DocumentUpload> Uploads { get; }
    DbSet<NotificationSinkEntry> NotificationSink { get; }
    ChangeTracker ChangeTracker { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public static class DbErrors
{
    /// <summary>True when the exception chain contains a PostgreSQL unique violation (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            var state = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
            if (state == "23505") return true;
            if (e.InnerException is null) break;
        }
        return false;
    }
}

/// <summary>Adds journal and outbox rows to the current unit of work (same transaction as the state change).</summary>
public sealed class EventRecorder(IOrchestratorDb db, IClock clock, ActorContext? actorContext = null)
{
    public const string OperationRequested = "OPERATION_REQUESTED";

    private readonly ActorContext _actor = actorContext ?? new ActorContext();

    /// <summary>Actor of events caused by the current API request (consumer or portal operator).</summary>
    public (string Type, string Id) Api => (_actor.Type, _actor.Id);

    public JournalEvent Journal(string processId, string type, string actorType, string actorId,
        string correlationId, string? causationId, object? metadata = null)
    {
        var ev = new JournalEvent
        {
            Id = Ids.New("evt"), ProcessId = processId, Type = type, OccurredAt = clock.UtcNow,
            ActorType = actorType, ActorId = actorId, CorrelationId = correlationId, CausationId = causationId,
            MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata)
        };
        db.Journal.Add(ev);
        return ev;
    }

    /// <summary>Messages carry references only (ids) - never documents or sensitive data.</summary>
    public OutboxEvent EnqueueOperation(Operation op, string correlationId, string? causationId, DateTime availableAt)
    {
        var ob = new OutboxEvent
        {
            Id = Ids.New("msg"), AggregateId = op.ProcessId, Type = OperationRequested, Queue = Resilience.OperationDomains.QueueFor(op.Type),
            AvailableAt = availableAt, CreatedAt = clock.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                processId = op.ProcessId, operationId = op.Id, correlationId, causationId, traceparent = System.Diagnostics.Activity.Current?.Id
            })
        };
        db.Outbox.Add(ob);
        return ob;
    }

    /// <summary>Reference-only message for the domain DLQ (published through the outbox like any other event).</summary>
    public OutboxEvent EnqueueDeadLetter(DeadLetterEntry entry, string correlationId, string? causationId)
    {
        var ob = new OutboxEvent
        {
            Id = Ids.New("msg"), AggregateId = entry.ProcessId, Type = "OPERATION_DEAD_LETTERED", Queue = entry.Queue,
            AvailableAt = clock.UtcNow, CreatedAt = clock.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                processId = entry.ProcessId, operationId = entry.OperationId, deadLetterId = entry.Id,
                domain = entry.Domain, errorClass = entry.ErrorClass.ToString(), correlationId, causationId
            })
        };
        db.Outbox.Add(ob);
        return ob;
    }
}

/// <summary>
/// Who triggered the current API request: CONSUMER/api by default, or OPERATOR/{X-Operator-Id} for portal actions.
/// Request scoped. (The identity from the access token replaces the header once authentication exists.)
/// </summary>
public sealed class ActorContext
{
    public string Type { get; set; } = "CONSUMER";
    public string Id { get; set; } = "api";
}

public sealed class SlaOptions
{
    /// <summary>Minutes a process may stay non-terminal before its SLA indicator becomes ALERT.</summary>
    public int SigningMinutes { get; set; } = 60;
}

/// <summary>
/// Authenticated caller of the current request (request scoped). ClientId is set only for machine clients,
/// which may see and operate just the processes they created; operators, viewers and admins are not restricted.
/// </summary>
public sealed class CallerContext
{
    public bool Authenticated { get; set; }
    public string UserId { get; set; } = "anonymous";
    public IReadOnlyCollection<string> Roles { get; set; } = [];
    public string? ClientId { get; set; }
}
