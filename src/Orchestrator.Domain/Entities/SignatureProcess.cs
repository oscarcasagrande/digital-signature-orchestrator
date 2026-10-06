using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Domain.Entities;

public class SignatureProcess
{
    public string Id { get; set; } = default!;
    public string ExternalId { get; set; } = default!;
    public BusinessStatus BusinessStatus { get; set; } = BusinessStatus.CREATED;
    public OperationalStatus OperationalStatus { get; set; } = OperationalStatus.READY;
    public string SignatureType { get; set; } = default!;
    public string RequestJson { get; set; } = "{}";
    public string? CallbackJson { get; set; }
    public string? IdentityValidationsJson { get; set; }
    public string CorrelationId { get; set; } = default!;
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public string? DocumentFileName { get; set; }
    /// <summary>Client (token azp) that created the process; null when created without authentication or by an operator.</summary>
    public string? ClientId { get; set; }
    public List<Signer> Signers { get; set; } = [];
    public List<Operation> Operations { get; set; } = [];

    public bool IsTerminal => BusinessStateMachine.IsTerminal(BusinessStatus);

    public void TransitionBusiness(BusinessStatus to, DateTime now)
    {
        BusinessStateMachine.Ensure(BusinessStatus, to);
        BusinessStatus = to;
        UpdatedAt = now;
        if (to == BusinessStatus.COMPLETED) CompletedAt = now;
    }

    public void TransitionOperational(OperationalStatus to, DateTime now)
    {
        if (OperationalStatus == to) return;
        OperationalStateMachine.Ensure(OperationalStatus, to);
        OperationalStatus = to;
        UpdatedAt = now;
    }
}
