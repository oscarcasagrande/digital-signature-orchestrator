using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Infrastructure.Providers.Real;

/// <summary>
/// Loads a <c>.env</c> file (current directory or a parent) into the process environment so API, Worker and tests read credentials
/// from environment variables only. Existing variables win; the file is git-ignored and documented in <c>.env.example</c>.
/// </summary>
public static class DotEnv
{
    public static int Load(string? startDirectory = null)
    {
        for (var dir = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(file)) continue;
            var loaded = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                else if (value.Contains(" #", StringComparison.Ordinal)) value = value[..value.IndexOf(" #", StringComparison.Ordinal)].TrimEnd();
                if (Environment.GetEnvironmentVariable(key) is null) { Environment.SetEnvironmentVariable(key, value); loaded++; }
            }
            return loaded;
        }
        return 0;
    }
}

/// <summary>Selection of the provider used for NEW processes. Existing processes keep the provider recorded in provider_process.</summary>
public sealed class ProviderOptions
{
    /// <summary>Simulated (default), DocuSign or Lacuna.</summary>
    public string Default { get; set; } = "Simulated";
}

/// <summary>Runtime holder of the default provider (initialised from <see cref="ProviderOptions"/>; tests flip it).</summary>
public sealed class ProviderSelection
{
    public string Default { get; set; } = ProviderCodes.Simulated;
}

public static class ProviderCodes
{
    public const string Simulated = "SIMULATED", DocuSign = "DOCUSIGN", Lacuna = "LACUNA";

    public static string Normalize(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "" or "SIMULATED" or "FAKE" => Simulated,
        "DOCUSIGN" => DocuSign,
        "LACUNA" or "LACUNASIGNER" => Lacuna,
        var other => throw new InvalidOperationException($"Unknown provider '{other}'. Use Simulated, DocuSign or Lacuna.")
    };
}

/// <summary>Credentials come from environment variables (flat names, see .env.example). Values are never logged or returned.</summary>
public sealed record DocuSignOptions(string? IntegrationKey, string? UserId, string? AccountId, string? PrivateKeyPem, string BaseUri,
    string AuthServer, string? ConnectHmacKey)
{
    public const string DefaultBaseUri = "https://demo.docusign.net/restapi";
    public const string DefaultAuthServer = "account-d.docusign.com";

    public static DocuSignOptions From(IConfiguration c) => new(
        Clean(c["DOCUSIGN_INTEGRATION_KEY"]), Clean(c["DOCUSIGN_USER_ID"]), Clean(c["DOCUSIGN_ACCOUNT_ID"]), ReadKey(c),
        (Clean(c["DOCUSIGN_BASE_URI"]) ?? DefaultBaseUri).TrimEnd('/'), Clean(c["DOCUSIGN_AUTH_SERVER"]) ?? DefaultAuthServer, Clean(c["DOCUSIGN_CONNECT_HMAC_KEY"]));

    public IReadOnlyList<string> Missing() => new[]
    {
        (IntegrationKey, "DOCUSIGN_INTEGRATION_KEY"), (UserId, "DOCUSIGN_USER_ID"), (AccountId, "DOCUSIGN_ACCOUNT_ID"),
        (PrivateKeyPem, "DOCUSIGN_PRIVATE_KEY or DOCUSIGN_PRIVATE_KEY_FILE")
    }.Where(x => string.IsNullOrWhiteSpace(x.Item1)).Select(x => x.Item2).ToList();

    private static string? ReadKey(IConfiguration c)
    {
        var file = Clean(c["DOCUSIGN_PRIVATE_KEY_FILE"]);
        if (file is not null && File.Exists(file)) return File.ReadAllText(file);
        return Clean(c["DOCUSIGN_PRIVATE_KEY"])?.Replace("\\n", "\n");
    }

    internal static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public sealed record LacunaOptions(string? ApiKey, string BaseUri, string? WebhookSecret)
{
    public const string DefaultBaseUri = "https://signer-lac.azurewebsites.net";

    public static LacunaOptions From(IConfiguration c) => new(
        DocuSignOptions.Clean(c["LACUNA_API_KEY"]), (DocuSignOptions.Clean(c["LACUNA_BASE_URI"]) ?? DefaultBaseUri).TrimEnd('/'),
        DocuSignOptions.Clean(c["LACUNA_WEBHOOK_SECRET"]));

    public IReadOnlyList<string> Missing() => string.IsNullOrWhiteSpace(ApiKey) ? ["LACUNA_API_KEY"] : [];
}

/// <summary>HTTP failure of a provider call, classified for the retry policy.</summary>
public sealed class ProviderHttpException(string message, int status, bool transient) : ProviderException(message, transient)
{
    public int Status { get; } = status;
}

public static class ProviderHttp
{
    /// <summary>Sends a request and turns failures into classified provider exceptions (429/408/5xx/401/network: transient; other 4xx: permanent).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string what, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new ProviderHttpException($"{what}: the provider is unreachable", 0, transient: true); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderHttpException($"{what}: the provider timed out", 0, transient: true); }
        if (res.IsSuccessStatusCode) return res;

        var status = (int)res.StatusCode;
        var body = "";
        try { body = await res.Content.ReadAsStringAsync(ct); } catch { /* body is optional */ }
        body = body.Length > 300 ? body[..300] : body;
        res.Dispose();
        var transient = status == 401 || Orchestrator.Application.Resilience.ErrorClassifier.FromHttpStatus(status) == ErrorClass.TRANSIENT;
        throw new ProviderHttpException($"{what}: HTTP {status} {body}".TrimEnd(), status, transient);
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}

/// <summary>Reads the original document the orchestrator stored, so adapters can hand it to the provider.</summary>
public sealed class OriginalDocumentReader(IOrchestratorDb db, ArtifactService artifacts)
{
    public sealed record OriginalDocument(byte[] Content, string ContentType, string FileName);

    public async Task<OriginalDocument> ReadAsync(string processId, CancellationToken ct)
    {
        var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.ProcessId == processId && x.Type == ArtifactType.ORIGINAL_DOCUMENT, ct)
                ?? throw new ProviderException("The original document is not stored yet", transient: true);
        return new OriginalDocument(await artifacts.ReadAsync(a, ct), a.ContentType, a.FileName);
    }
}
