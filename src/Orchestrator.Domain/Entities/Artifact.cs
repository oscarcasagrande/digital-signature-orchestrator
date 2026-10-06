namespace Orchestrator.Domain.Entities;

public enum ArtifactType { ORIGINAL_DOCUMENT, SIGNED_DOCUMENT, EVIDENCE, DOCUMENT_FRONT, DOCUMENT_BACK, SELFIE }

public class Artifact
{
    public string Id { get; set; } = default!;
    public string ProcessId { get; set; } = default!;
    public ArtifactType Type { get; set; }
    public string ContentType { get; set; } = default!;
    public string Sha256 { get; set; } = default!;
    public long Size { get; set; }
    /// <summary>Internal object key. Never exposed through the API.</summary>
    public string StorageKey { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
