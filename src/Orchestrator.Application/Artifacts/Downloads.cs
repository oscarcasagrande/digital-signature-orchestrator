using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Application.Artifacts;

public enum LinkCheck { Valid, Invalid, Expired }

public sealed class InvalidLinkException() : Exception("Download link is invalid");
public sealed class LinkExpiredException() : Exception("Download link has expired");

/// <summary>HMAC-SHA256 signed, short-lived, single-artifact links. No storage keys or credentials appear in them.</summary>
public sealed class DownloadLinkSigner(IOptions<ArtifactOptions> options, IClock clock)
{
    public (string Url, DateTime ExpiresAt) Issue(string artifactId)
    {
        var expiresAt = clock.UtcNow.AddSeconds(options.Value.LinkTtlSeconds);
        var expires = new DateTimeOffset(expiresAt, TimeSpan.Zero).ToUnixTimeSeconds();
        var url = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/v1/downloads/{artifactId}?expires={expires}&sig={Sign(artifactId, expires)}";
        return (url, expiresAt);
    }

    public LinkCheck Verify(string artifactId, long expires, string? sig)
    {
        if (string.IsNullOrEmpty(sig)) return LinkCheck.Invalid;
        var expected = Encoding.ASCII.GetBytes(Sign(artifactId, expires));
        var given = Encoding.ASCII.GetBytes(sig);
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return LinkCheck.Invalid;
        return DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime <= clock.UtcNow ? LinkCheck.Expired : LinkCheck.Valid;
    }

    private string Sign(string artifactId, long expires)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes($"{artifactId}.{expires}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record DownloadLinkRequest(string? ArtifactId, string? Type);
public sealed record DownloadLinkResult(string Url, DateTime ExpiresAt);
public sealed record ArtifactDto(string ArtifactId, string Type, string ContentType, string Sha256, long Size, string FileName, DateTime CreatedAt);

public sealed class ArtifactQueries(IOrchestratorDb db)
{
    public async Task<IReadOnlyList<ArtifactDto>> ListAsync(string processId, CancellationToken ct)
    {
        if (!await db.Processes.AnyAsync(p => p.Id == processId, ct)) throw new NotFoundException("Process");
        var rows = await db.Artifacts.AsNoTracking().Where(a => a.ProcessId == processId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public static ArtifactDto Map(Artifact a) =>
        new(a.Id, a.Type.ToString(), a.ContentType, a.Sha256, a.Size, a.FileName, a.CreatedAt);
}

public sealed record OpenedDownload(Artifact Artifact, StoredObject Content);

public sealed class DownloadService(IOrchestratorDb db, IArtifactStore store, DownloadLinkSigner signer, EventRecorder recorder)
{
    public async Task<DownloadLinkResult> IssueLinkAsync(string processId, DownloadLinkRequest? request, string correlationId, CancellationToken ct)
    {
        var process = await db.Processes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == processId, ct)
                      ?? throw new NotFoundException("Process");
        var artifacts = db.Artifacts.AsNoTracking().Where(a => a.ProcessId == processId);

        Artifact? artifact;
        if (!string.IsNullOrWhiteSpace(request?.ArtifactId))
            artifact = await artifacts.FirstOrDefaultAsync(a => a.Id == request.ArtifactId, ct);
        else if (!string.IsNullOrWhiteSpace(request?.Type))
        {
            if (!Enum.TryParse<ArtifactType>(request.Type, out var type))
                throw new ValidationException([new FieldError("type", "Unknown artifact type")]);
            artifact = await artifacts.FirstOrDefaultAsync(a => a.Type == type, ct);
        }
        else
        {
            var wanted = process.BusinessStatus == Domain.StateMachines.BusinessStatus.COMPLETED
                ? ArtifactType.SIGNED_DOCUMENT : ArtifactType.ORIGINAL_DOCUMENT;
            artifact = await artifacts.FirstOrDefaultAsync(a => a.Type == wanted, ct);
        }
        if (artifact is null) throw new NotFoundException("Artifact");

        var (url, expiresAt) = signer.Issue(artifact.Id);
        recorder.Journal(processId, "DOWNLOAD_LINK_ISSUED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
            new { artifactId = artifact.Id, type = artifact.Type.ToString(), expiresAt });
        await db.SaveChangesAsync(ct);
        return new DownloadLinkResult(url, expiresAt);
    }

    public async Task<OpenedDownload> OpenAsync(string artifactId, long expires, string? sig, string correlationId, CancellationToken ct)
    {
        switch (signer.Verify(artifactId, expires, sig))
        {
            case LinkCheck.Invalid: throw new InvalidLinkException();
            case LinkCheck.Expired: throw new LinkExpiredException();
        }
        var artifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct)
                       ?? throw new NotFoundException("Artifact");
        var content = await store.GetAsync(artifact.StorageKey, ct);
        recorder.Journal(artifact.ProcessId, "DOCUMENT_DOWNLOADED", "CONSUMER", "download-link", correlationId, null,
            new { artifactId = artifact.Id, type = artifact.Type.ToString(), sha256 = artifact.Sha256 });
        await db.SaveChangesAsync(ct);
        return new OpenedDownload(artifact, content);
    }
}
