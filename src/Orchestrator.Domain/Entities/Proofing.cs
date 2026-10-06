namespace Orchestrator.Domain.Entities;

public enum ProofingSessionStatus { WAITING_EVIDENCE, IN_PROGRESS, COMPLETED }

public enum ProofingResult { PENDING, APPROVED, REJECTED }

public enum ValidationStatus { WAITING_EVIDENCE, PENDING, PASSED, FAILED, ERROR }

public class ProofingSession
{
    public string Id { get; set; } = default!;
    public string ExternalId { get; set; } = default!;
    public ProofingSessionStatus Status { get; set; } = ProofingSessionStatus.WAITING_EVIDENCE;
    public ProofingResult Result { get; set; } = ProofingResult.PENDING;
    /// <summary>Subject data (name, document, phone, email, deviceId) as JSON. Personal data: masked in every response.</summary>
    public string SubjectJson { get; set; } = "{}";
    public string CorrelationId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? EvidenceDeletedAt { get; set; }
    /// <summary>Client (token azp) that owns the session; null when created without authentication or by an admin.</summary>
    public string? ClientId { get; set; }
    public List<IdentityValidation> Validations { get; set; } = [];
}

public class IdentityValidation
{
    public string Id { get; set; } = default!;
    public string SessionId { get; set; } = default!;
    public string Capability { get; set; } = default!;
    public bool Required { get; set; } = true;
    public ValidationStatus Status { get; set; } = ValidationStatus.WAITING_EVIDENCE;
    public double? Score { get; set; }
    /// <summary>Normalized provider details; never contains personal data.</summary>
    public string DetailsJson { get; set; } = "{}";
    public string? ProviderCode { get; set; }
    public string? OperationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public bool IsTerminal => Status is ValidationStatus.PASSED or ValidationStatus.FAILED;
}
