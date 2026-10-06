using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Infrastructure.Providers.Real;

/// <summary>
/// Lacuna Signer REST adapter (demo environment by default; API key in <c>X-Api-Key</c>). The document is uploaded and a signature
/// flow is created with one "Signer" action per signer (order = signer order, parallel when none). The Signer API creates a flow
/// that starts immediately, so the whole creation happens in <see cref="SendDocumentAsync"/>; <see cref="CreateProcessAsync"/> only
/// registers the process. Status words are matched loosely (case-insensitive prefixes) because the vendor enum names vary by version.
/// </summary>
public sealed class LacunaAdapter(IOrchestratorDb db, IHttpClientFactory http, LacunaOptions options, OriginalDocumentReader documents, IClock clock) : IProviderAdapter
{
    public string Code => ProviderCodes.Lacuna;
    public Task<bool> SupportsPerSignerReleaseAsync(string externalReference, CancellationToken ct) => Task.FromResult(false);
    public Task AddSignerAsync(string externalReference, ProviderSigner signer, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseSignerAsync(string externalReference, int position, CancellationToken ct) => Task.CompletedTask;

    private void RequireCredentials()
    {
        if (options.Missing().Count > 0)
            throw new ProviderException("Lacuna is selected but credentials are missing: " + string.Join(", ", options.Missing()), transient: false);
    }

    private async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, string what, HttpContent? content, CancellationToken ct)
    {
        RequireCredentials();
        var req = new HttpRequestMessage(method, options.BaseUri + path) { Content = content };
        req.Headers.Add("X-Api-Key", options.ApiKey);
        return await ProviderHttp.SendAsync(http.CreateClient("lacuna"), req, what, ct);
    }

    private async Task<ProviderProcess> LoadAsync(string reference, CancellationToken ct) =>
        await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == reference, ct)
        ?? throw new ProviderException($"Provider process not found for reference {reference}");

    private sealed record Meta(string? DocumentId, int Signers, List<SignerInfo> People);
    private sealed record SignerInfo(int Position, string Name, string? Email, string? Document, int? Order);

    private static Meta ReadMeta(ProviderProcess pp) => JsonSerializer.Deserialize<Meta>(pp.MetadataJson, JsonOpts)!;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest r, CancellationToken ct)
    {
        RequireCredentials();
        var existing = db.ProviderProcesses.FirstOrDefault(x => x.ExternalReference == r.ExternalReference);
        if (existing is not null) return Task.FromResult(new ProviderProcessInfo(existing.ProviderProcessId, existing.NormalizedStatus, existing.MetadataJson));
        var noEmail = r.Signers.Where(s => string.IsNullOrWhiteSpace(s.Email)).Select(s => s.Position + 1).ToList();
        if (noEmail.Count > 0)
            throw new ProviderException("Lacuna needs an e-mail for every signer (missing for signer " + string.Join(", ", noEmail) + ")", transient: false);

        var meta = new Meta(null, r.Signers.Count, r.Signers.Select(s => new SignerInfo(s.Position, s.Name, s.Email, s.Document, s.Order)).ToList());
        var pp = new ProviderProcess
        {
            ProcessId = r.ExternalReference, ProviderCode = Code, ExternalReference = r.ExternalReference,
            ProviderProcessId = "pending:" + r.ExternalReference, NormalizedStatus = ProviderStatuses.Pending,
            MetadataJson = JsonSerializer.Serialize(meta), UpdatedAt = clock.UtcNow
        };
        db.ProviderProcesses.Add(pp);
        return Task.FromResult(new ProviderProcessInfo(pp.ProviderProcessId, pp.NormalizedStatus, pp.MetadataJson));
    }

    public async Task SendDocumentAsync(string externalReference, string fileName, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        var meta = ReadMeta(pp);
        if (meta.DocumentId is not null) return; // idempotent

        var doc = await documents.ReadAsync(externalReference, ct);
        var upload = new ByteArrayContent(doc.Content);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var up = await CallAsync(HttpMethod.Post, "/api/uploads", "Lacuna upload", upload, ct);
        var uploadId = JsonNode.Parse(await up.Content.ReadAsStringAsync(ct))?["id"]?.GetValue<string>()
                       ?? throw new ProviderException("Lacuna returned no upload id", transient: false);

        var body = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["displayName"] = fileName, ["id"] = uploadId, ["name"] = fileName, ["contentType"] = doc.ContentType }),
            ["description"] = "orchestrator:" + externalReference,
            ["flowActions"] = new JsonArray(meta.People.Select(s => (JsonNode)new JsonObject
            {
                ["type"] = "Signer",
                ["step"] = s.Order ?? 1,
                ["user"] = new JsonObject { ["name"] = s.Name, ["identifier"] = s.Document, ["email"] = s.Email },
                ["allowElectronicSignature"] = true
            }).ToArray())
        };
        using var res = await CallAsync(HttpMethod.Post, "/api/documents", "Lacuna create document", ProviderHttp.Json(body.ToJsonString()), ct);
        var parsed = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var documentId = (parsed is JsonArray a ? a.FirstOrDefault() : parsed)?["documentId"]?.GetValue<string>()
                         ?? throw new ProviderException("Lacuna returned no documentId", transient: false);

        pp.ProviderProcessId = documentId;
        pp.MetadataJson = JsonSerializer.Serialize(meta with { DocumentId = documentId });
        pp.UpdatedAt = clock.UtcNow;
    }

    public async Task<ProviderStatusResult> GetStatusAsync(string externalReference, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        var meta = ReadMeta(pp);
        if (meta.DocumentId is null) return new ProviderStatusResult(ProviderStatuses.Pending, [], pp.MetadataJson);

        using var res = await CallAsync(HttpMethod.Get, $"/api/documents/{meta.DocumentId}", "Lacuna get document", null, ct);
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var status = json?["status"]?.GetValue<string>() ?? "";
        var actions = json?["flowActions"]?.AsArray().ToList() ?? [];
        var signed = new List<int>();
        for (var i = 0; i < actions.Count && i < meta.People.Count; i++)
        {
            var st = actions[i]?["status"]?.GetValue<string>() ?? "";
            if (Starts(st, "complet", "conclu", "signed")) signed.Add(meta.People[i].Position);
        }

        var normalized = status switch
        {
            _ when Starts(status, "conclu", "complet") => ProviderStatuses.Signed,
            _ when Starts(status, "cancel") => ProviderStatuses.Cancelled,
            _ when Starts(status, "refus", "reject", "declin") => ProviderStatuses.Rejected,
            _ when signed.Count > 0 => ProviderStatuses.PartiallySigned,
            _ => ProviderStatuses.Pending
        };
        if (normalized == ProviderStatuses.Signed) signed = meta.People.Select(p => p.Position).Order().ToList();
        if (normalized is ProviderStatuses.Rejected or ProviderStatuses.Cancelled) signed = [];

        pp.NormalizedStatus = normalized;
        pp.UpdatedAt = clock.UtcNow;
        return new ProviderStatusResult(normalized, signed.Order().ToList(), pp.MetadataJson);
    }

    private static bool Starts(string value, params string[] prefixes) => prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public async Task CancelAsync(string externalReference, CancellationToken ct)
    {
        var pp = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == externalReference, ct);
        if (pp is null) return;
        var meta = ReadMeta(pp);
        if (meta.DocumentId is not null && pp.NormalizedStatus is not (ProviderStatuses.Signed or ProviderStatuses.Cancelled))
        {
            using var _ = await CallAsync(HttpMethod.Post, $"/api/documents/{meta.DocumentId}/cancellation",
                "Lacuna cancel document", ProviderHttp.Json("""{"reason":"Cancelled by the orchestrator"}"""), ct);
        }
        pp.NormalizedStatus = ProviderStatuses.Cancelled;
        pp.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProviderFile> DownloadSignedDocumentAsync(string externalReference, byte[] originalContent, string originalContentType, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        if (pp.NormalizedStatus != ProviderStatuses.Signed)
            throw new ProviderException($"Document is not signed yet (status {pp.NormalizedStatus})", transient: true);
        using var res = await CallAsync(HttpMethod.Get, $"/api/documents/{ReadMeta(pp).DocumentId}/content?type=Signatures", "Lacuna download signed document", null, ct);
        return new ProviderFile(await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? originalContentType, "signed-" + externalReference + ".pdf");
    }

    public async Task<ProviderFile> DownloadEvidenceAsync(string externalReference, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        using var res = await CallAsync(HttpMethod.Get, $"/api/documents/{ReadMeta(pp).DocumentId}/content?type=PrinterFriendlyVersion", "Lacuna download evidence", null, ct);
        return new ProviderFile(await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "application/pdf", "signature-report-" + externalReference + ".pdf");
    }
}

/// <summary>Lacuna webhook: shared secret in <c>X-Webhook-Secret</c> (constant-time compare); the document id is read from the JSON body.</summary>
public sealed class LacunaWebhookHandler(LacunaOptions options) : IProviderWebhookHandler
{
    public string ProviderCode => ProviderCodes.Lacuna;

    public ProviderWebhookResult Verify(IReadOnlyDictionary<string, string> headers, string body)
    {
        if (string.IsNullOrEmpty(options.WebhookSecret)) return new(false, null, null);
        var given = headers.FirstOrDefault(h => h.Key.Equals("X-Webhook-Secret", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        var ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(options.WebhookSecret)));
        if (!ok) return new(false, null, null);
        try
        {
            var json = JsonNode.Parse(body);
            var id = json?["documentId"]?.GetValue<string>() ?? json?["data"]?["documentId"]?.GetValue<string>() ?? json?["id"]?.GetValue<string>();
            return new(true, id, json?["type"]?.GetValue<string>() ?? json?["event"]?.GetValue<string>());
        }
        catch (JsonException) { return new(true, null, null); }
    }
}
