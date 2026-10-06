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
/// OAuth JWT Grant for DocuSign: signs an RS256 assertion with the integration private key and caches the access token until
/// shortly before it expires. The key and token are never logged.
/// </summary>
public sealed class DocuSignTokenProvider(DocuSignOptions options, IHttpClientFactory http, IClock clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _expires;

    public void Invalidate() => _token = null;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_token is not null && clock.UtcNow < _expires) return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.UtcNow < _expires) return _token;
            var missing = options.Missing();
            if (missing.Count > 0)
                throw new ProviderException("DocuSign is selected but credentials are missing: " + string.Join(", ", missing), transient: false);

            var authHost = options.AuthServer.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? options.AuthServer.TrimEnd('/') : "https://" + options.AuthServer;
            var audience = new Uri(authHost).Host;
            var assertion = BuildAssertion(options, audience, clock.UtcNow);
            var req = new HttpRequestMessage(HttpMethod.Post, authHost + "/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion
                })
            };
            using var res = await ProviderHttp.SendAsync(http.CreateClient("docusign"), req, "DocuSign authentication", ct);
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            _token = json?["access_token"]?.GetValue<string>() ?? throw new ProviderException("DocuSign authentication returned no access token", transient: false);
            var seconds = json?["expires_in"]?.GetValue<int>() ?? 3600;
            _expires = clock.UtcNow.AddSeconds(Math.Max(30, seconds - 120));
            return _token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>JWT assertion (RFC 7523) for the DocuSign JWT Grant.</summary>
    public static string BuildAssertion(DocuSignOptions o, string audience, DateTime now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var iat = new DateTimeOffset(now).ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = o.IntegrationKey, sub = o.UserId, aud = audience, iat, exp = iat + 3600, scope = "signature impersonation"
        })));
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(o.PrivateKeyPem!); }
        catch (ArgumentException) { throw new ProviderException("DOCUSIGN_PRIVATE_KEY is not a valid PEM RSA private key", transient: false); }
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + B64(signature);
    }
}

/// <summary>
/// DocuSign eSignature REST v2.1 adapter (demo environment by default). The envelope is created as a draft with the original
/// document and recipients, sent on <see cref="SendDocumentAsync"/>, and normalised on <see cref="GetStatusAsync"/>.
/// Recipient id = signer position + 1; routing order = signer order (parallel when none).
/// </summary>
public sealed class DocuSignAdapter(IOrchestratorDb db, IHttpClientFactory http, DocuSignOptions options, DocuSignTokenProvider tokens,
    OriginalDocumentReader documents, IClock clock) : IProviderAdapter
{
    public string Code => ProviderCodes.DocuSign;
    public Task<bool> SupportsPerSignerReleaseAsync(string externalReference, CancellationToken ct) => Task.FromResult(false);
    public Task AddSignerAsync(string externalReference, ProviderSigner signer, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseSignerAsync(string externalReference, int position, CancellationToken ct) => Task.CompletedTask;

    private string Url(string path) => $"{options.BaseUri}/v2.1/accounts/{options.AccountId}{path}";

    private async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, string what, HttpContent? content, CancellationToken ct, string? accept = null)
    {
        var token = await tokens.GetAsync(ct);
        var req = new HttpRequestMessage(method, Url(path)) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept is not null) req.Headers.Accept.ParseAdd(accept);
        try { return await ProviderHttp.SendAsync(http.CreateClient("docusign"), req, what, ct); }
        catch (ProviderHttpException ex) when (ex.Status == 401) { tokens.Invalidate(); throw; }
    }

    private async Task<JsonNode?> GetJsonAsync(string path, string what, CancellationToken ct)
    {
        using var res = await CallAsync(HttpMethod.Get, path, what, null, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<ProviderProcess> LoadAsync(string reference, CancellationToken ct) =>
        await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == reference, ct)
        ?? throw new ProviderException($"Provider process not found for reference {reference}");

    public async Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest r, CancellationToken ct)
    {
        var existing = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == r.ExternalReference, ct);
        if (existing is not null) return new ProviderProcessInfo(existing.ProviderProcessId, existing.NormalizedStatus, existing.MetadataJson);

        if (options.Missing().Count > 0)
            throw new ProviderException("DocuSign is selected but credentials are missing: " + string.Join(", ", options.Missing()), transient: false);
        var noEmail = r.Signers.Where(s => string.IsNullOrWhiteSpace(s.Email)).Select(s => s.Position + 1).ToList();
        if (noEmail.Count > 0)
            throw new ProviderException("DocuSign needs an e-mail for every signer (missing for signer " + string.Join(", ", noEmail) + ")", transient: false);

        // A previous attempt may have created the envelope and failed before saving: look it up by the custom field first.
        var found = await FindByReferenceAsync(r.ExternalReference, ct);
        var envelopeId = found;
        if (envelopeId is null)
        {
            var doc = await documents.ReadAsync(r.ExternalReference, ct);
            var body = new JsonObject
            {
                ["emailSubject"] = "Please sign: " + doc.FileName,
                ["status"] = "created",
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["documentBase64"] = Convert.ToBase64String(doc.Content), ["name"] = doc.FileName, ["fileExtension"] = Path.GetExtension(doc.FileName).TrimStart('.') is { Length: > 0 } ext ? ext : "pdf",
                    ["documentId"] = "1"
                }),
                ["recipients"] = new JsonObject
                {
                    ["signers"] = new JsonArray(r.Signers.Select(s => (JsonNode)new JsonObject
                    {
                        ["email"] = s.Email, ["name"] = s.Name, ["recipientId"] = (s.Position + 1).ToString(),
                        ["routingOrder"] = (s.Order ?? 1).ToString(),
                        ["tabs"] = new JsonObject
                        {
                            ["signHereTabs"] = new JsonArray(new JsonObject
                            {
                                ["documentId"] = "1", ["pageNumber"] = "1", ["xPosition"] = "100", ["yPosition"] = (200 + s.Position * 60).ToString()
                            })
                        }
                    }).ToArray())
                },
                ["customFields"] = new JsonObject
                {
                    ["textCustomFields"] = new JsonArray(new JsonObject { ["name"] = "orchestratorRef", ["value"] = r.ExternalReference, ["show"] = "false" })
                }
            };
            using var res = await CallAsync(HttpMethod.Post, "/envelopes", "DocuSign create envelope", ProviderHttp.Json(body.ToJsonString()), ct);
            envelopeId = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["envelopeId"]?.GetValue<string>()
                         ?? throw new ProviderException("DocuSign returned no envelopeId", transient: false);
        }

        var meta = JsonSerializer.Serialize(new { envelopeId, signers = r.Signers.Count, status = "created" });
        var pp = new ProviderProcess
        {
            ProcessId = r.ExternalReference, ProviderCode = Code, ExternalReference = r.ExternalReference, ProviderProcessId = envelopeId,
            NormalizedStatus = ProviderStatuses.Pending, MetadataJson = meta, UpdatedAt = clock.UtcNow
        };
        db.ProviderProcesses.Add(pp);
        return new ProviderProcessInfo(pp.ProviderProcessId, pp.NormalizedStatus, pp.MetadataJson);
    }

    private async Task<string?> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        var from = Uri.EscapeDataString(clock.UtcNow.AddDays(-30).ToString("yyyy-MM-dd"));
        var json = await GetJsonAsync($"/envelopes?from_date={from}&custom_field=orchestratorRef%3D{Uri.EscapeDataString(reference)}", "DocuSign find envelope", ct);
        return json?["envelopes"]?.AsArray().FirstOrDefault()?["envelopeId"]?.GetValue<string>();
    }

    public async Task SendDocumentAsync(string externalReference, string fileName, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        var current = (await GetJsonAsync($"/envelopes/{pp.ProviderProcessId}", "DocuSign get envelope", ct))?["status"]?.GetValue<string>();
        if (current is not null && !current.Equals("created", StringComparison.OrdinalIgnoreCase)) return; // already sent (idempotent)
        using var _ = await CallAsync(HttpMethod.Put, $"/envelopes/{pp.ProviderProcessId}", "DocuSign send envelope", ProviderHttp.Json("""{"status":"sent"}"""), ct);
    }

    public async Task<ProviderStatusResult> GetStatusAsync(string externalReference, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        var json = await GetJsonAsync($"/envelopes/{pp.ProviderProcessId}?include=recipients", "DocuSign get envelope", ct);
        var status = (json?["status"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var signers = json?["recipients"]?["signers"]?.AsArray().ToList() ?? [];
        var signedPositions = signers.Where(s => string.Equals(s?["status"]?.GetValue<string>(), "completed", StringComparison.OrdinalIgnoreCase))
            .Select(s => int.TryParse(s!["recipientId"]?.GetValue<string>(), out var id) ? id - 1 : -1).Where(p => p >= 0).Order().ToList();

        var normalized = status switch
        {
            "completed" => ProviderStatuses.Signed,
            "declined" => ProviderStatuses.Rejected,
            "voided" => ProviderStatuses.Cancelled,
            _ when signedPositions.Count > 0 => ProviderStatuses.PartiallySigned,
            _ => ProviderStatuses.Pending
        };
        if (normalized == ProviderStatuses.Signed && signers.Count > 0) signedPositions = signers.Select(s => int.Parse(s!["recipientId"]!.GetValue<string>()) - 1).Order().ToList();
        if (normalized is ProviderStatuses.Rejected or ProviderStatuses.Cancelled) signedPositions = [];

        pp.NormalizedStatus = normalized;
        pp.UpdatedAt = clock.UtcNow;
        pp.MetadataJson = JsonSerializer.Serialize(new { envelopeId = pp.ProviderProcessId, status, signers = signers.Count });
        return new ProviderStatusResult(normalized, signedPositions, pp.MetadataJson);
    }

    public async Task CancelAsync(string externalReference, CancellationToken ct)
    {
        var pp = await db.ProviderProcesses.FirstOrDefaultAsync(x => x.ExternalReference == externalReference, ct);
        if (pp is null) return;
        var status = (await GetJsonAsync($"/envelopes/{pp.ProviderProcessId}", "DocuSign get envelope", ct))?["status"]?.GetValue<string>()?.ToLowerInvariant();
        if (status is "created" or "completed" or "voided" or "declined") // drafts were never sent; finished envelopes cannot be voided
        {
            pp.NormalizedStatus = status == "created" ? ProviderStatuses.Cancelled : pp.NormalizedStatus;
            await db.SaveChangesAsync(ct);
            return;
        }
        using var _ = await CallAsync(HttpMethod.Put, $"/envelopes/{pp.ProviderProcessId}", "DocuSign void envelope",
            ProviderHttp.Json("""{"status":"voided","voidedReason":"Cancelled by the orchestrator"}"""), ct);
        pp.NormalizedStatus = ProviderStatuses.Cancelled;
        pp.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProviderFile> DownloadSignedDocumentAsync(string externalReference, byte[] originalContent, string originalContentType, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        if (pp.NormalizedStatus != ProviderStatuses.Signed)
            throw new ProviderException($"Document is not signed yet (status {pp.NormalizedStatus})", transient: true);
        using var res = await CallAsync(HttpMethod.Get, $"/envelopes/{pp.ProviderProcessId}/documents/combined", "DocuSign download signed document", null, ct, "application/pdf");
        return new ProviderFile(await res.Content.ReadAsByteArrayAsync(ct), "application/pdf", "signed-" + externalReference + ".pdf");
    }

    public async Task<ProviderFile> DownloadEvidenceAsync(string externalReference, CancellationToken ct)
    {
        var pp = await LoadAsync(externalReference, ct);
        using var res = await CallAsync(HttpMethod.Get, $"/envelopes/{pp.ProviderProcessId}/documents/certificate", "DocuSign download certificate", null, ct, "application/pdf");
        return new ProviderFile(await res.Content.ReadAsByteArrayAsync(ct), "application/pdf", "certificate-of-completion.pdf");
    }
}

/// <summary>DocuSign Connect: HMAC-SHA256 (base64) of the raw body in <c>X-DocuSign-Signature-N</c>, JSON (SIM) payload.</summary>
public sealed class DocuSignWebhookHandler(DocuSignOptions options) : IProviderWebhookHandler
{
    public string ProviderCode => ProviderCodes.DocuSign;

    public ProviderWebhookResult Verify(IReadOnlyDictionary<string, string> headers, string body)
    {
        if (string.IsNullOrEmpty(options.ConnectHmacKey)) return new(false, null, null);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ConnectHmacKey), Encoding.UTF8.GetBytes(body));
        var ok = false;
        foreach (var (name, value) in headers)
        {
            if (!name.StartsWith("X-DocuSign-Signature-", StringComparison.OrdinalIgnoreCase)) continue;
            try { ok |= CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(value.Trim())); }
            catch (FormatException) { /* not base64: not a match */ }
        }
        if (!ok) return new(false, null, null);
        try
        {
            var json = JsonNode.Parse(body);
            return new(true, json?["data"]?["envelopeId"]?.GetValue<string>() ?? json?["envelopeId"]?.GetValue<string>(), json?["event"]?.GetValue<string>());
        }
        catch (JsonException) { return new(true, null, null); }
    }
}
