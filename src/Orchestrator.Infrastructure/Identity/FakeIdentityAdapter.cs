using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Infrastructure.Identity;

/// <summary>
/// Simulated identity provider: deterministic outcomes driven by markers in the evidence and subject data, plus
/// failure injection through the session external id prefix. No external call is made and no personal data is returned.
/// </summary>
public sealed class FakeIdentityAdapter : IIdentityProofingAdapter
{
    public const string ProviderCode = "SIMULATED";
    public string Code => ProviderCode;

    public Task<IdentityValidationOutcome> ValidateAsync(IdentityValidationRequest r, CancellationToken ct)
    {
        InjectFailures(r);
        if (r.Capability == IdentityCapabilities.IdentityRisk) return Task.FromResult(Risk(r));

        var (passed, reason) = Evaluate(r);
        var score = Score(r.Capability, r.Subject.Document, passed);
        var details = JsonSerializer.Serialize(new { provider = ProviderCode, capability = r.Capability, simulated = true, reason });
        return Task.FromResult(new IdentityValidationOutcome(passed, score, details));
    }

    private static void InjectFailures(IdentityValidationRequest r)
    {
        var flaky = Regex.Match(r.ExternalId, @"^SIM-FLAKY-(\d+)-");
        if (flaky.Success && r.Attempt <= int.Parse(flaky.Groups[1].Value))
            throw new ProviderException($"Simulated transient identity provider failure (attempt {r.Attempt})", transient: true);
        if (r.ExternalId.StartsWith("SIM-DOWN-", StringComparison.Ordinal))
            throw new ProviderException("Simulated identity provider outage (HTTP 503)", transient: true);
        if (r.ExternalId.StartsWith("SIM-UNKNOWN-", StringComparison.Ordinal))
            throw new InvalidProgramException("Simulated unexpected identity provider failure");
        if (r.ExternalId.StartsWith("SIM-FAIL-", StringComparison.Ordinal))
            throw new ProviderException("Simulated identity provider rejection of the request", transient: false);
    }

    private static (bool Passed, string Reason) Evaluate(IdentityValidationRequest r)
    {
        string Text(ArtifactType t) => r.Evidence.TryGetValue(t, out var b) ? Encoding.UTF8.GetString(b) : "";
        return r.Capability switch
        {
            "PERSON_DATA" => r.Subject.Document.EndsWith("0000", StringComparison.Ordinal) ? (false, "person_data_mismatch") : (true, "ok"),
            "DOCUMENT_AUTHENTICITY" => Text(ArtifactType.DOCUMENT_FRONT).Contains("FAKE_FORGED") ? (false, "document_not_authentic") : (true, "ok"),
            "DOCUMENT_OWNERSHIP" => Text(ArtifactType.DOCUMENT_FRONT).Contains("FAKE_NOT_OWNER") ? (false, "document_owner_mismatch") : (true, "ok"),
            "FACE_MATCH" => Text(ArtifactType.SELFIE).Contains("FAKE_FACE_MISMATCH") ? (false, "face_mismatch") : (true, "ok"),
            "LIVENESS" => Text(ArtifactType.SELFIE).Contains("FAKE_SPOOF") ? (false, "liveness_failed") : (true, "ok"),
            "GOVERNMENT_BIOMETRIC_MATCH" => Text(ArtifactType.SELFIE).Contains("FAKE_NO_MATCH") ? (false, "official_base_no_match") : (true, "ok"),
            "PHONE_OWNERSHIP" => r.Subject.Phone!.EndsWith("0000", StringComparison.Ordinal) ? (false, "phone_not_owned") : (true, "ok"),
            "EMAIL_OWNERSHIP" => r.Subject.Email!.Contains("fraud", StringComparison.OrdinalIgnoreCase) ? (false, "email_not_owned") : (true, "ok"),
            "DEVICE_RISK" => r.Subject.DeviceId!.StartsWith("risky-", StringComparison.Ordinal) ? (false, "risky_device") : (true, "ok"),
            _ => (true, "ok") // DOCUMENT_DATA and any capability without a failure marker
        };
    }

    /// <summary>Deterministic score: 0.80 to 0.99 when the check passes, 0.00 to 0.29 when it fails.</summary>
    private static double Score(string capability, string document, bool passed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(capability + document));
        return passed ? Math.Round(0.80 + h[0] % 20 / 100.0, 2) : Math.Round(h[0] % 30 / 100.0, 2);
    }

    private static IdentityValidationOutcome Risk(IdentityValidationRequest r)
    {
        var prior = r.Prior;
        var average = prior.Count == 0 ? 1.0 : prior.Average(p => p.Passed ? p.Score ?? 0.9 : 0.0);
        var risk = Math.Round(1 - average, 4);
        var details = JsonSerializer.Serialize(new
        {
            provider = ProviderCode, capability = r.Capability, simulated = true, risk, considered = prior.Count,
            failed = prior.Count(p => !p.Passed)
        });
        return new IdentityValidationOutcome(risk < 0.5, Math.Round(1 - risk, 4), details);
    }
}

/// <summary>Applies the evidence retention policy periodically (runs in the Worker).</summary>
public sealed class EvidenceRetentionService(IServiceScopeFactory scopes, IOptions<IdentityOptions> options, ILogger<EvidenceRetentionService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var purged = await scope.ServiceProvider.GetRequiredService<EvidencePurger>().PurgeExpiredAsync(stop);
                if (purged > 0) log.LogInformation("Retention policy removed {Count} evidence item(s)", purged);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Evidence retention sweep failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.RetentionSweepMinutes)), stop); }
            catch (OperationCanceledException) { }
        }
    }
}
