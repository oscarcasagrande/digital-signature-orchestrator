namespace Orchestrator.Domain.Entities;

public enum DeliveryStatus { PENDING, RETRY_PENDING, DELIVERED, FAILED, DLQ }

public class CallbackRegistration
{
    public string CallbackId { get; set; } = default!;
    public string Url { get; set; } = default!;
    /// <summary>Shared HMAC secret. Returned only when the registration is created; never logged.</summary>
    public string Secret { get; set; } = default!;
    public bool Active { get; set; } = true;
    public string? Description { get; set; }
    /// <summary>Owner client (token azp); null for registrations made by admins, which every caller may use.</summary>
    public string? ClientId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CallbackDelivery
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public string OperationId { get; set; } = default!;
    /// <summary>Stable across attempts so receivers can deduplicate.</summary>
    public string EventId { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public string ProcessStatus { get; set; } = default!;
    /// <summary>callbackId or URL without query/credentials. Never contains secrets.</summary>
    public string Destination { get; set; } = default!;
    public DeliveryStatus Status { get; set; } = DeliveryStatus.PENDING;
    public int Attempts { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}
