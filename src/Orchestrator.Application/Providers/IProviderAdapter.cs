namespace Orchestrator.Application.Providers;

/// <summary>Signer as the provider needs to know it. Email, Document (CPF) and Order are optional so providers that do not need them are unaffected.</summary>
public sealed record ProviderSigner(int Position, string Name, string? Email = null, string? Document = null, int? Order = null);

public sealed record CreateProviderProcessRequest(
    string ExternalReference, string ExternalId, string SignatureType, IReadOnlyList<ProviderSigner> Signers, int Attempt = 1, bool Gated = false);

public sealed record ProviderProcessInfo(string ProviderProcessId, string NormalizedStatus, string MetadataJson);

public sealed record ProviderStatusResult(
    string NormalizedStatus, IReadOnlyList<int> SignedPositions, string MetadataJson);

public static class ProviderStatuses
{
    public const string Pending = "PENDING";
    public const string PartiallySigned = "PARTIALLY_SIGNED";
    public const string Signed = "SIGNED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Failure of an operation. Transient failures may be retried (policy arrives with spec 003).</summary>
public class OperationException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}

public class ProviderException(string message, bool transient = false) : OperationException(message, transient)
{
}

/// <summary>
/// Common contract for signature providers. Implementations MUST be idempotent per ExternalReference
/// and MUST NOT leak vendor types into the domain.
/// </summary>
public interface IProviderAdapter
{
    string Code { get; }
    Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest request, CancellationToken ct);
    Task SendDocumentAsync(string externalReference, string fileName, CancellationToken ct);
    Task AddSignerAsync(string externalReference, ProviderSigner signer, CancellationToken ct);
    /// <summary>Authorizes one signer (by position) to sign. Only meaningful for processes created with Gated=true; idempotent.</summary>
    Task ReleaseSignerAsync(string externalReference, int position, CancellationToken ct);
    /// <summary>
    /// True when signers can be authorized one by one (ReleaseSignerAsync). Providers that cannot (real e-signature vendors)
    /// only receive the document once every confirmation of every signer is done; ordering is then delegated to the provider routing.
    /// </summary>
    Task<bool> SupportsPerSignerReleaseAsync(string externalReference, CancellationToken ct);
    Task<ProviderStatusResult> GetStatusAsync(string externalReference, CancellationToken ct);
    Task CancelAsync(string externalReference, CancellationToken ct);
    Task<ProviderFile> DownloadSignedDocumentAsync(string externalReference, byte[] originalContent, string originalContentType, CancellationToken ct);
    Task<ProviderFile> DownloadEvidenceAsync(string externalReference, CancellationToken ct);
}

public sealed record ProviderFile(byte[] Content, string ContentType, string FileName);

/// <summary>Outcome of validating an inbound provider webhook.</summary>
public sealed record ProviderWebhookResult(bool Authentic, string? ProviderProcessId, string? EventType);

/// <summary>
/// Validates the authenticity of a provider webhook and extracts the provider reference. The payload is never a source of
/// truth: callers only use it as a trigger to reconcile against the provider.
/// </summary>
public interface IProviderWebhookHandler
{
    string ProviderCode { get; }
    ProviderWebhookResult Verify(IReadOnlyDictionary<string, string> headers, string body);
}
