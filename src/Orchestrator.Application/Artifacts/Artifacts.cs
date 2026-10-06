using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Application.Artifacts;

public sealed class ArtifactOptions
{
    /// <summary>HMAC key for download links. The default is for local development only.</summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-0123456789";
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    public int LinkTtlSeconds { get; set; } = 300;
    public long MaxDocumentBytes { get; set; } = 50L * 1024 * 1024;
    public int FetchTimeoutSeconds { get; set; } = 30;
}

public sealed record StoredObject(Stream Content, string ContentType, long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Port for the S3-compatible object store.</summary>
public interface IArtifactStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Task<StoredObject> GetAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed record FetchedDocument(byte[] Content, string ContentType);

public interface IDocumentFetcher
{
    Task<FetchedDocument> FetchAsync(Uri source, long maxBytes, CancellationToken ct);
}

public static class ArtifactKeys
{
    public static string For(string processId, ArtifactType type) => type switch
    {
        ArtifactType.ORIGINAL_DOCUMENT => $"{processId}/input/original",
        ArtifactType.SIGNED_DOCUMENT => $"{processId}/output/signed",
        ArtifactType.EVIDENCE => $"{processId}/evidence/evidence.json",
        ArtifactType.DOCUMENT_FRONT => $"{processId}/identity/document-front",
        ArtifactType.DOCUMENT_BACK => $"{processId}/identity/document-back",
        ArtifactType.SELFIE => $"{processId}/identity/selfie",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

/// <summary>Stores artifacts: object first, then the (idempotent) metadata row in the caller's unit of work.</summary>
public sealed class ArtifactService(IOrchestratorDb db, IArtifactStore store, IClock clock, IOptions<ArtifactOptions> options)
{
    public async Task<Artifact> StoreAsync(string processId, ArtifactType type, byte[] content, string contentType,
        string fileName, CancellationToken ct)
    {
        if (content.LongLength > options.Value.MaxDocumentBytes)
            throw new OperationException($"Artifact exceeds the {options.Value.MaxDocumentBytes} byte limit", transient: false);

        var existing = await db.Artifacts.FirstOrDefaultAsync(a => a.ProcessId == processId && a.Type == type, ct);
        if (existing is not null) return existing;

        var key = ArtifactKeys.For(processId, type);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        try { await store.PutAsync(key, content, contentType, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OperationException($"Artifact store unavailable: {ex.Message}", transient: true);
        }

        var artifact = new Artifact
        {
            Id = Ids.New("art"), ProcessId = processId, Type = type, ContentType = contentType, Sha256 = hash,
            Size = content.LongLength, StorageKey = key, FileName = fileName, CreatedAt = clock.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new { stored = "internal" })
        };
        db.Artifacts.Add(artifact);
        return artifact;
    }

    /// <summary>Reads an uploaded source document (the upload was validated when the process was created).</summary>
    public async Task<FetchedDocument> ReadUploadAsync(string uploadId, CancellationToken ct)
    {
        var up = await db.Uploads.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uploadId, ct)
                 ?? throw new OperationException($"Upload {uploadId} not found", transient: false);
        try
        {
            await using var obj = await store.GetAsync(up.StorageKey, ct);
            using var ms = new MemoryStream();
            await obj.Content.CopyToAsync(ms, ct);
            return new FetchedDocument(ms.ToArray(), up.ContentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OperationException($"Artifact store unavailable: {ex.Message}", transient: true);
        }
    }

    public async Task<byte[]> ReadAsync(Artifact artifact, CancellationToken ct)
    {
        await using var obj = await store.GetAsync(artifact.StorageKey, ct);
        using var ms = new MemoryStream();
        await obj.Content.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task<Artifact> RequireStoredAsync(string processId, ArtifactType type, CancellationToken ct)
    {
        var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.ProcessId == processId && x.Type == type, ct)
                ?? throw new OperationException($"Artifact {type} is not registered", transient: false);
        if (!await store.ExistsAsync(a.StorageKey, ct))
            throw new OperationException($"Artifact {type} object is missing from the store", transient: true);
        return a;
    }
}

public sealed record UploadDto(string UploadId, string FileName, string ContentType, long Size, string Sha256, DateTime ExpiresAt);

/// <summary>Documents sent before the process exists (<c>document.source = {type: UPLOAD, uploadId}</c>); owned by the uploading client.</summary>
public sealed class DocumentUploadService(IOrchestratorDb db, IArtifactStore store, IClock clock, IOptions<ArtifactOptions> options, CallerContext? caller = null)
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    public async Task<UploadDto> StoreAsync(string fileName, string contentType, byte[] content, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(fileName)) errors.Add(new("file", "A file name is required"));
        else if (fileName.Length > 300) errors.Add(new("file", "File name is too long (max 300)"));
        if (content.Length == 0) errors.Add(new("file", "The file is empty"));
        if (content.LongLength > options.Value.MaxDocumentBytes) errors.Add(new("file", $"The file exceeds the {options.Value.MaxDocumentBytes} byte limit"));
        if (errors.Count > 0) throw new ValidationException(errors);

        var id = Ids.New("upl");
        var key = "uploads/" + id;
        await store.PutAsync(key, content, contentType, ct);
        var up = new DocumentUpload
        {
            Id = id, ClientId = caller?.ClientId, FileName = fileName, ContentType = contentType, Size = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), StorageKey = key, CreatedAt = clock.UtcNow
        };
        db.Uploads.Add(up);
        await db.SaveChangesAsync(ct);
        return new UploadDto(up.Id, up.FileName, up.ContentType, up.Size, up.Sha256, up.CreatedAt + Validity);
    }

    /// <summary>Checks existence, ownership and validity at process creation. The message never reveals other clients' uploads.</summary>
    public async Task<FieldError?> CheckAsync(string uploadId, CancellationToken ct)
    {
        var up = await db.Uploads.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uploadId, ct);
        var foreign = up?.ClientId is { } owner && caller?.ClientId is { } me && owner != me;
        if (up is null || foreign || clock.UtcNow - up.CreatedAt > Validity)
            return new FieldError("document.source.uploadId", "Unknown or expired upload");
        return null;
    }
}
