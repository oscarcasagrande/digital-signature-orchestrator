using System.Text.Json.Nodes;
using Orchestrator.Application.Processes;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Application.Identity;

public sealed class IdentityOptions
{
    public long MaxEvidenceBytes { get; set; } = 5L * 1024 * 1024;
    public int EvidenceRetentionDays { get; set; } = 30;
    public int RetentionSweepMinutes { get; set; } = 60;
}

public sealed record ProofingSubject(string Name, string Document, string? Phone, string? Email, string? DeviceId);

public sealed record PriorResult(string Capability, bool Passed, double? Score);

/// <summary>Vendor-neutral request. Evidence bytes exist only in memory for the duration of the call.</summary>
public sealed record IdentityValidationRequest(string SessionReference, string ExternalId, string Capability, ProofingSubject Subject,
    IReadOnlyDictionary<ArtifactType, byte[]> Evidence, IReadOnlyList<PriorResult> Prior, int Attempt);

public sealed record IdentityValidationOutcome(bool Passed, double? Score, string DetailsJson);

/// <summary>Common contract for identity providers. Implementations must not leak vendor types or personal data into results.</summary>
public interface IIdentityProofingAdapter
{
    string Code { get; }
    Task<IdentityValidationOutcome> ValidateAsync(IdentityValidationRequest request, CancellationToken ct);
}

/// <summary>The capability catalog (PRD section 9) with the evidence and subject data each one needs.</summary>
public static class IdentityCapabilities
{
    public const string IdentityRisk = "IDENTITY_RISK";

    public static readonly string[] All =
    [
        "PERSON_DATA", "DOCUMENT_DATA", "DOCUMENT_AUTHENTICITY", "DOCUMENT_OWNERSHIP", "FACE_MATCH", "LIVENESS",
        "GOVERNMENT_BIOMETRIC_MATCH", "PHONE_OWNERSHIP", "EMAIL_OWNERSHIP", "DEVICE_RISK", IdentityRisk
    ];

    public static readonly ArtifactType[] EvidenceTypes = [ArtifactType.DOCUMENT_FRONT, ArtifactType.DOCUMENT_BACK, ArtifactType.SELFIE];

    public static IReadOnlyList<ArtifactType> EvidenceFor(string capability) => capability switch
    {
        "DOCUMENT_DATA" or "DOCUMENT_AUTHENTICITY" or "DOCUMENT_OWNERSHIP" => [ArtifactType.DOCUMENT_FRONT],
        "LIVENESS" or "GOVERNMENT_BIOMETRIC_MATCH" => [ArtifactType.SELFIE],
        "FACE_MATCH" => [ArtifactType.SELFIE, ArtifactType.DOCUMENT_FRONT],
        _ => []
    };

    /// <summary>Subject field the capability needs besides name and document, or null.</summary>
    public static string? SubjectFieldFor(string capability) => capability switch
    {
        "PHONE_OWNERSHIP" => "phone",
        "EMAIL_OWNERSHIP" => "email",
        "DEVICE_RISK" => "deviceId",
        _ => null
    };

    /// <summary>True when the validation can start: its evidence is present (or, for IDENTITY_RISK, all others finished).</summary>
    public static bool IsReady(string capability, IReadOnlySet<ArtifactType> evidence, IEnumerable<IdentityValidation> all)
    {
        if (capability == IdentityRisk)
            return all.Where(v => v.Capability != IdentityRisk).All(v => v.IsTerminal);
        return EvidenceFor(capability).All(evidence.Contains);
    }
}

public sealed record RequestedValidation(string Capability, bool Required);

public sealed record ProofingRequest(string ExternalId, ProofingSubject Subject, IReadOnlyList<RequestedValidation> Validations);

public static class ProofingRequestValidator
{
    private static readonly string[] ForbiddenVendorFields = ["provider", "providers", "providerId", "vendor"];

    public static (ProofingRequest? Request, List<FieldError> Errors) Parse(JsonNode? root)
    {
        var errors = new List<FieldError>();
        if (root is not JsonObject obj) return (null, [new("body", "Body must be a JSON object")]);

        foreach (var f in ForbiddenVendorFields)
            if (obj.ContainsKey(f)) errors.Add(new(f, "Consumers request capabilities, never specific providers"));

        var externalId = Str(obj["externalId"]);
        if (string.IsNullOrWhiteSpace(externalId)) errors.Add(new("externalId", "Required"));
        else if (externalId.Length > 200) errors.Add(new("externalId", "Max length is 200"));

        string? name = null, document = null, phone = null, email = null, deviceId = null;
        if (obj["subject"] is not JsonObject s) errors.Add(new("subject", "Required"));
        else
        {
            name = Str(s["name"]);
            if (string.IsNullOrWhiteSpace(name)) errors.Add(new("subject.name", "Required"));
            else if (name.Length > 300) errors.Add(new("subject.name", "Max length is 300"));
            document = Str(s["document"]);
            if (!CreateProcessValidator.IsValidCpf(document)) errors.Add(new("subject.document", "Invalid CPF"));
            phone = Str(s["phone"]);
            if (phone is not null && (phone.Length is < 10 or > 15 || !phone.All(char.IsDigit))) errors.Add(new("subject.phone", "Must have 10 to 15 digits"));
            email = Str(s["email"]);
            if (email is not null && (email.Length > 200 || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@')))
                errors.Add(new("subject.email", "Invalid e-mail"));
            deviceId = Str(s["deviceId"]);
            if (deviceId is { Length: > 100 }) errors.Add(new("subject.deviceId", "Max length is 100"));
        }

        var validations = new List<RequestedValidation>();
        if (obj["validations"] is not JsonArray arr || arr.Count == 0) errors.Add(new("validations", "At least one validation is required"));
        else
            for (var i = 0; i < arr.Count; i++)
            {
                string? type; var required = true;
                if (arr[i] is JsonObject vo)
                {
                    type = Str(vo["type"]);
                    if (vo["required"] is JsonValue rv && rv.TryGetValue<bool>(out var rb)) required = rb;
                }
                else type = Str(arr[i]);

                if (type is null || !IdentityCapabilities.All.Contains(type)) { errors.Add(new($"validations[{i}]", "Unknown capability")); continue; }
                if (validations.Any(v => v.Capability == type)) { errors.Add(new($"validations[{i}]", "Duplicate capability")); continue; }
                var field = IdentityCapabilities.SubjectFieldFor(type);
                if (field == "phone" && phone is null || field == "email" && email is null || field == "deviceId" && deviceId is null)
                    errors.Add(new($"subject.{field}", $"Required by {type}"));
                validations.Add(new RequestedValidation(type, required));
            }

        return errors.Count > 0 ? (null, errors)
            : (new ProofingRequest(externalId!, new ProofingSubject(name!, document!, phone, email, deviceId), validations), errors);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
