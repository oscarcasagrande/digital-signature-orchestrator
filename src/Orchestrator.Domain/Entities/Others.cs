using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Domain.Entities;

public class Signer
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public string ExternalId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Document { get; set; } = default!;
    public int Position { get; set; }
    public bool Signed { get; set; }
    public DateTime? SignedAt { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>Effective signature type (own, process default or process-level type).</summary>
    public string? SignatureType { get; set; }
    /// <summary>Sequential signing order (1-based); null means the signer is not ordered (parallel).</summary>
    public int? Order { get; set; }
    /// <summary>Comma-separated confirmation channels required before signing (EMAIL, SMS, WHATSAPP); empty when none.</summary>
    public string ConfirmationChannels { get; set; } = "";
    /// <summary>When the signer was released at the provider (confirmed and, if ordered, their turn).</summary>
    public DateTime? ReleasedAt { get; set; }

    public IReadOnlyList<string> Channels => ConfirmationChannels.Length == 0 ? [] : ConfirmationChannels.Split(',');
}

public class Operation
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public OperationType Type { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.NOT_STARTED;
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public DateTime? NextRetryAt { get; set; }
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? ErrorJson { get; set; }
    public ErrorClass? ErrorClass { get; set; }
    public int Sequence { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class JournalEvent
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public long Seq { get; set; }
    public string Type { get; set; } = default!;
    public DateTime OccurredAt { get; set; }
    public string ActorType { get; set; } = default!;
    public string ActorId { get; set; } = default!;
    public string MetadataJson { get; set; } = "{}";
    public string CorrelationId { get; set; } = default!;
    public string? CausationId { get; set; }
}

public class OutboxEvent
{
    public string Id { get; set; } = default!;
    public string AggregateId { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string PayloadJson { get; set; } = "{}";
    public string Queue { get; set; } = default!;
    public DateTime AvailableAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class InboxMessage
{
    public string Consumer { get; set; } = default!;
    public string MessageId { get; set; } = default!;
    public DateTime ReceivedAt { get; set; }
}

public class IdempotencyRecord
{
    public string Key { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}

public class ProviderProcess
{
    public string ProcessId { get; set; } = default!;
    public string ProviderCode { get; set; } = default!;
    public string ProviderProcessId { get; set; } = default!;
    public string ExternalReference { get; set; } = default!;
    public string NormalizedStatus { get; set; } = default!;
    public string MetadataJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; }
}

public class DeadLetterEntry
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public string OperationId { get; set; } = default!;
    public string OperationType { get; set; } = default!;
    public string Domain { get; set; } = default!;
    public string Queue { get; set; } = default!;
    public ErrorClass ErrorClass { get; set; }
    /// <summary>Short reason (max 500 chars) - never contains documents or personal data.</summary>
    public string Reason { get; set; } = default!;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
