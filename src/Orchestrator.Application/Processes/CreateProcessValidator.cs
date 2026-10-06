using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orchestrator.Application.Processes;

public sealed record FieldError(string Field, string Message);

public sealed class ValidationException(IReadOnlyList<FieldError> errors) : Exception("Validation failed")
{
    public IReadOnlyList<FieldError> Errors { get; } = errors;
}

public sealed class IdempotencyConflictException() : Exception("Idempotency-Key was already used with a different payload");

/// <summary>Request conflicts with the current state of the resource (HTTP 409).</summary>
public sealed class ConflictException(string message) : Exception(message);

public static class CreateProcessValidator
{
    public static readonly string[] SignatureTypes = SignerPlan.SignatureTypes;

    public static readonly string[] Capabilities =
    [
        "PERSON_DATA", "DOCUMENT_DATA", "DOCUMENT_AUTHENTICITY", "DOCUMENT_OWNERSHIP", "FACE_MATCH", "LIVENESS",
        "GOVERNMENT_BIOMETRIC_MATCH", "PHONE_OWNERSHIP", "EMAIL_OWNERSHIP", "DEVICE_RISK", "IDENTITY_RISK"
    ];

    private static readonly string[] ForbiddenVendorFields = ["provider", "providers", "providerId", "vendor"];

    public static List<FieldError> Validate(JsonNode? root)
    {
        var errors = new List<FieldError>();
        if (root is not JsonObject obj) { errors.Add(new("body", "Body must be a JSON object")); return errors; }

        foreach (var f in ForbiddenVendorFields)
            if (obj.ContainsKey(f))
                errors.Add(new(f, "Consumers request capabilities, never specific providers"));

        var externalId = Str(obj["externalId"]);
        if (string.IsNullOrWhiteSpace(externalId)) errors.Add(new("externalId", "Required"));
        else if (externalId.Length > 200) errors.Add(new("externalId", "Max length is 200"));

        if (obj["document"] is not JsonObject doc) errors.Add(new("document", "Required"));
        else
        {
            if (string.IsNullOrWhiteSpace(Str(doc["fileName"]))) errors.Add(new("document.fileName", "Required"));
            if (doc["source"] is not JsonObject src) errors.Add(new("document.source", "Required"));
            else
            {
                var srcType = Str(src["type"]);
                if (srcType == "UPLOAD")
                {
                    if (string.IsNullOrWhiteSpace(Str(src["uploadId"]))) errors.Add(new("document.source.uploadId", "Required"));
                }
                else if (srcType != "URL") errors.Add(new("document.source.type", "Must be URL or UPLOAD"));
                else if (!Uri.TryCreate(Str(src["url"]), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    errors.Add(new("document.source.url", "Must be an absolute http(s) URL"));
            }
        }

        errors.AddRange(SignerPlan.Resolve(obj).Errors);

        if (obj["identityProofing"] is JsonObject ip && ip["validations"] is JsonArray vals)
            for (var i = 0; i < vals.Count; i++)
            {
                var type = vals[i] is JsonObject vo ? Str(vo["type"]) : Str(vals[i]);
                if (type is null || !Capabilities.Contains(type))
                    errors.Add(new($"identityProofing.validations[{i}]", "Unknown capability"));
            }

        if (obj["callback"] is JsonObject cb)
        {
            var url = Str(cb["url"]);
            var callbackId = Str(cb["callbackId"]);
            if (url is null && callbackId is null) errors.Add(new("callback", "url or callbackId required"));
            if (url is not null && callbackId is not null) errors.Add(new("callback", "Inform either callbackId or url, not both"));
            // The destination policy (https, SSRF rules, registered callbackId) is enforced by CreateProcessHandler.
        }

        return errors;
    }

    public static bool IsValidCpf(string? cpf)
    {
        if (cpf is null || cpf.Length != 11 || !cpf.All(char.IsDigit) || cpf.Distinct().Count() == 1) return false;
        for (var len = 9; len <= 10; len++)
        {
            var sum = 0;
            for (var i = 0; i < len; i++) sum += (cpf[i] - '0') * (len + 1 - i);
            var dv = sum * 10 % 11 % 10;
            if (cpf[len] - '0' != dv) return false;
        }
        return true;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Canonical JSON (object keys sorted ordinally, no whitespace) used to hash payloads.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? n, StringBuilder sb)
    {
        switch (n)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i], sb); }
                sb.Append(']');
                break;
            default: sb.Append(n.ToJsonString()); break;
        }
    }

    public static string Hash(JsonNode? node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(node)))).ToLowerInvariant();
}
