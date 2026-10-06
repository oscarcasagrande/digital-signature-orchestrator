using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Domain.Entities;

/// <summary>One required confirmation (signer + channel). Created PLANNED with the process; the code itself is never stored, only its HMAC.</summary>
public class SignerConfirmation
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public string SignerId { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public ConfirmationStatus Status { get; set; } = ConfirmationStatus.PLANNED;
    public string? CodeHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public int SendCount { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}

/// <summary>Document uploaded before a process exists; referenced by <c>document.source = {type: UPLOAD, uploadId}</c>.</summary>
public class DocumentUpload
{
    public string Id { get; set; } = default!;
    public string? ClientId { get; set; }
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long Size { get; set; }
    public string Sha256 { get; set; } = default!;
    public string StorageKey { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Messages "delivered" by the simulated notifier, readable by tests and development tooling only.</summary>
public class NotificationSinkEntry
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public string SignerId { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public string Code { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}
