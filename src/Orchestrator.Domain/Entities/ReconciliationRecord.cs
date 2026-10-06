namespace Orchestrator.Domain.Entities;

/// <summary>Audit row for a reconciliation that corrected a divergence or was requested manually.</summary>
public class ReconciliationRecord
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    /// <summary>SCHEDULED or MANUAL.</summary>
    public string Trigger { get; set; } = default!;
    public string InternalStatus { get; set; } = default!;
    public string? ProviderStatus { get; set; }
    /// <summary>CORRECTED, CONSISTENT, NOT_APPLICABLE or PROVIDER_ERROR.</summary>
    public string Outcome { get; set; } = default!;
    public string? ResultingStatus { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
