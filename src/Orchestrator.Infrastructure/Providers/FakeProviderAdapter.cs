using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Infrastructure.Providers;

public sealed class FakeProviderOptions
{
    /// <summary>Auto | Reject | Fail. A process externalId prefixed with SIM-REJECT- / SIM-FAIL- overrides it.</summary>
    public string Mode { get; set; } = "Auto";
    /// <summary>Seconds after the document is sent until each signer signs (signer k signs at (k+1) * delay).</summary>
    public double SignDelaySeconds { get; set; } = 2;
}

/// <summary>
/// Simulated signature provider. State lives in provider_process (stateless instances), the adapter is
/// idempotent per external reference, and no external service is called.
/// </summary>
public sealed class FakeProviderAdapter(IOrchestratorDb db, IClock clock, IOptions<FakeProviderOptions> options) : IProviderAdapter
{
    public const string ProviderCode = "SIMULATED";
    public string Code => ProviderCode;

    private sealed record Meta(string Mode, int Signers, string ExternalId, DateTime? SentAt, string Status, bool Cancelled,
        bool Gated = false, Dictionary<int, DateTime>? Released = null);

    public async Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest r, CancellationToken ct)
    {
        var existing = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == r.ExternalReference, ct);
        if (existing is not null)
            return new ProviderProcessInfo(existing.ProviderProcessId, existing.NormalizedStatus, existing.MetadataJson);

        // Failure injection by external id prefix (simulation only).
        var flaky = System.Text.RegularExpressions.Regex.Match(r.ExternalId, @"^SIM-FLAKY-(\d+)-");
        if (flaky.Success && r.Attempt <= int.Parse(flaky.Groups[1].Value))
            throw new ProviderException($"Simulated transient provider failure (attempt {r.Attempt})", transient: true);
        if (r.ExternalId.StartsWith("SIM-DOWN-", StringComparison.Ordinal))
            throw new ProviderException("Simulated provider outage (HTTP 503)", transient: true);
        if (r.ExternalId.StartsWith("SIM-UNKNOWN-", StringComparison.Ordinal))
            throw new InvalidProgramException("Simulated unexpected failure");

        var mode = r.ExternalId.StartsWith("SIM-FAIL-", StringComparison.Ordinal) ? "Fail"
            : r.ExternalId.StartsWith("SIM-REJECT-", StringComparison.Ordinal) ? "Reject" : options.Value.Mode;
        if (mode == "Fail") throw new ProviderException("Simulated provider failure on create");

        var meta = new Meta(mode, r.Signers.Count, r.ExternalId, null, ProviderStatuses.Pending, false, r.Gated);
        var pp = new ProviderProcess
        {
            ProcessId = r.ExternalReference, ProviderCode = ProviderCode, ExternalReference = r.ExternalReference,
            ProviderProcessId = "sim_" + Guid.NewGuid().ToString("N")[..16], NormalizedStatus = ProviderStatuses.Pending,
            MetadataJson = JsonSerializer.Serialize(meta), UpdatedAt = clock.UtcNow
        };
        db.ProviderProcesses.Add(pp);
        return new ProviderProcessInfo(pp.ProviderProcessId, pp.NormalizedStatus, pp.MetadataJson);
    }

    public Task AddSignerAsync(string externalReference, ProviderSigner signer, CancellationToken ct) => Task.CompletedTask;

    public async Task SendDocumentAsync(string externalReference, string fileName, CancellationToken ct)
    {
        var pp = await Load(externalReference, ct);
        var meta = Read(pp);
        if (meta.SentAt is not null) return; // idempotent
        Save(pp, meta with { SentAt = clock.UtcNow }, meta.Status);
    }

    public Task<bool> SupportsPerSignerReleaseAsync(string externalReference, CancellationToken ct) => Task.FromResult(true);

    public async Task ReleaseSignerAsync(string externalReference, int position, CancellationToken ct)
    {
        var pp = await Load(externalReference, ct);
        var meta = Read(pp);
        var released = meta.Released ?? new Dictionary<int, DateTime>();
        if (released.ContainsKey(position)) return; // idempotent
        released[position] = clock.UtcNow;
        Save(pp, meta with { Released = released }, pp.NormalizedStatus);
    }

    public async Task<ProviderStatusResult> GetStatusAsync(string externalReference, CancellationToken ct)
    {
        var pp = await Load(externalReference, ct);
        var meta = Read(pp);
        if (meta.Cancelled) return new ProviderStatusResult(ProviderStatuses.Cancelled, [], pp.MetadataJson);
        if (meta.SentAt is null) return new ProviderStatusResult(ProviderStatuses.Pending, [], pp.MetadataJson);

        var step = Math.Max(options.Value.SignDelaySeconds, 0.001);
        List<int> signedPositions;
        if (meta.Gated)
        {
            // Explicit release: a signer signs SignDelaySeconds after the orchestrator released them.
            var now = clock.UtcNow;
            signedPositions = (meta.Released ?? []).Where(kv => (now - kv.Value).TotalSeconds >= step).Select(kv => kv.Key).Order().ToList();
        }
        else
        {
            var elapsed = (clock.UtcNow - meta.SentAt.Value).TotalSeconds;
            signedPositions = Enumerable.Range(0, Math.Clamp((int)Math.Floor(elapsed / step), 0, meta.Signers)).ToList();
        }
        var signedCount = signedPositions.Count;
        string status;
        if (meta.Mode == "Reject" && signedCount >= 1) { status = ProviderStatuses.Rejected; signedPositions = []; }
        else if (signedCount >= meta.Signers) status = ProviderStatuses.Signed;
        else if (signedCount > 0) status = ProviderStatuses.PartiallySigned;
        else status = ProviderStatuses.Pending;

        if (status != pp.NormalizedStatus) Save(pp, meta with { Status = status }, status);
        return new ProviderStatusResult(status, signedPositions, pp.MetadataJson);
    }

    public async Task CancelAsync(string externalReference, CancellationToken ct)
    {
        var pp = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == externalReference, ct);
        if (pp is null) return;
        Save(pp, Read(pp) with { Cancelled = true, Status = ProviderStatuses.Cancelled }, ProviderStatuses.Cancelled);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProviderFile> DownloadSignedDocumentAsync(string externalReference, byte[] originalContent,
        string originalContentType, CancellationToken ct)
    {
        var pp = await Load(externalReference, ct);
        var meta = Read(pp);
        if (pp.NormalizedStatus != ProviderStatuses.Signed)
            throw new ProviderException($"Document is not signed yet (status {pp.NormalizedStatus})", transient: true);
        var trailer = System.Text.Encoding.UTF8.GetBytes(
            $"\n%% SIGNED-BY-SIMULATED-PROVIDER ref={externalReference} signers={meta.Signers}\n");
        return new ProviderFile(originalContent.Concat(trailer).ToArray(), originalContentType, "signed-" + externalReference);
    }

    public async Task<ProviderFile> DownloadEvidenceAsync(string externalReference, CancellationToken ct)
    {
        var pp = await Load(externalReference, ct);
        var meta = Read(pp);
        var json = JsonSerializer.Serialize(new
        {
            provider = ProviderCode, providerProcessId = pp.ProviderProcessId, reference = externalReference,
            status = pp.NormalizedStatus, signers = meta.Signers, simulated = true
        });
        return new ProviderFile(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "evidence.json");
    }

    private async Task<ProviderProcess> Load(string reference, CancellationToken ct) =>
        await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == reference, ct)
        ?? throw new ProviderException($"Provider process not found for reference {reference}");

    private static Meta Read(ProviderProcess pp) => JsonSerializer.Deserialize<Meta>(pp.MetadataJson)!;

    private void Save(ProviderProcess pp, Meta meta, string status)
    {
        pp.MetadataJson = JsonSerializer.Serialize(meta);
        pp.NormalizedStatus = status;
        pp.UpdatedAt = clock.UtcNow;
    }
}
