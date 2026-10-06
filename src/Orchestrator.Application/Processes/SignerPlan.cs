using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orchestrator.Application.Processes;

/// <summary>A signer after defaults were applied (what is stored and what drives confirmation and ordering).</summary>
public sealed record EffectiveSigner(int Position, string ExternalId, string Name, string Document, string? Email, string? Phone,
    string SignatureType, int? Order, IReadOnlyList<string> Channels);

/// <summary>
/// Resolves the signers of a creation payload: applies process defaults, validates contacts, channels, ordering and CPF.
/// Every error points at <c>signers[i].field</c>. Pure function, no I/O.
/// </summary>
public static partial class SignerPlan
{
    public static readonly string[] SignatureTypes = ["SIMPLE", "ADVANCED", "QUALIFIED"];
    public static readonly string[] ChannelNames = ["EMAIL", "SMS", "WHATSAPP"];

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Any(c => !(char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '.'))) return null;
        var plus = raw.TrimStart().StartsWith('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length is < 10 or > 15) return null;
        return (plus ? "+" : "") + digits;
    }

    public static (List<EffectiveSigner> Signers, List<FieldError> Errors) Resolve(JsonObject root)
    {
        var errors = new List<FieldError>();
        var result = new List<EffectiveSigner>();

        string? defaultType = null;
        IReadOnlyList<string>? defaultChannels = null;
        if (root["defaults"] is { } dn)
        {
            if (dn is not JsonObject d) errors.Add(new("defaults", "Must be an object"));
            else
            {
                if (d["signatureType"] is not null)
                {
                    defaultType = UpperStr(d["signatureType"]);
                    if (defaultType is null || !SignatureTypes.Contains(defaultType))
                    { errors.Add(new("defaults.signatureType", $"Must be one of {string.Join(", ", SignatureTypes)}")); defaultType = null; }
                }
                if (d["confirmation"] is not null)
                    defaultChannels = ParseChannels(d["confirmation"], "defaults.confirmation", errors);
            }
        }

        string? legacyType = null;
        if (root["signature"] is JsonObject sig)
        {
            legacyType = UpperStr(sig["type"]);
            if (legacyType is null || !SignatureTypes.Contains(legacyType))
            { errors.Add(new("signature.type", $"Must be one of {string.Join(", ", SignatureTypes)}")); legacyType = null; }
        }
        else if (root["signature"] is not null) errors.Add(new("signature", "Must be an object"));

        if (root["signers"] is not JsonArray arr || arr.Count == 0)
        {
            errors.Add(new("signers", "At least one signer is required"));
            return (result, errors);
        }

        var orders = new int?[arr.Count];
        var anyOrder = false;
        for (var i = 0; i < arr.Count; i++)
        {
            var f = $"signers[{i}]";
            if (arr[i] is not JsonObject s) { errors.Add(new(f, "Must be an object")); continue; }

            var name = Str(s["name"]);
            if (string.IsNullOrWhiteSpace(name)) errors.Add(new($"{f}.name", "Required"));
            else if (name.Length > 300) errors.Add(new($"{f}.name", "Max length is 300"));

            var doc = Str(s["document"]);
            if (!CreateProcessValidator.IsValidCpf(doc)) errors.Add(new($"{f}.document", "Invalid CPF"));

            var externalId = Str(s["externalId"]);
            if (s["externalId"] is not null && string.IsNullOrWhiteSpace(externalId)) errors.Add(new($"{f}.externalId", "Must be a non-empty string"));
            else if (externalId is { Length: > 200 }) errors.Add(new($"{f}.externalId", "Max length is 200"));

            var email = Str(s["email"])?.Trim();
            if (s["email"] is not null && (email is null || email.Length > 254 || !EmailPattern().IsMatch(email)))
            { errors.Add(new($"{f}.email", "Invalid email")); email = null; }
            var phone = NormalizePhone(Str(s["phone"]));
            if (s["phone"] is not null && phone is null) errors.Add(new($"{f}.phone", "Invalid phone (10 to 15 digits, optional leading +)"));

            string? own = null;
            if (s["signatureType"] is not null)
            {
                own = UpperStr(s["signatureType"]);
                if (own is null || !SignatureTypes.Contains(own))
                { errors.Add(new($"{f}.signatureType", $"Must be one of {string.Join(", ", SignatureTypes)}")); own = null; }
            }
            var type = own ?? defaultType ?? legacyType ?? "";
            if (type == "" && s["signatureType"] is null)
                errors.Add(new($"{f}.signatureType", "Required (inform it on the signer, in defaults.signatureType or in signature.type)"));

            IReadOnlyList<string> channels = defaultChannels ?? [];
            if (s["confirmation"] is not null) channels = ParseChannels(s["confirmation"], $"{f}.confirmation", errors);
            if (channels.Contains("EMAIL") && s["email"] is null)
                errors.Add(new($"{f}.email", "Required when the confirmation channel EMAIL is used"));
            if ((channels.Contains("SMS") || channels.Contains("WHATSAPP")) && s["phone"] is null)
                errors.Add(new($"{f}.phone", "Required when the confirmation channel SMS or WHATSAPP is used"));

            int? order = null;
            if (s["order"] is not null)
            {
                anyOrder = true;
                if (s["order"] is JsonValue ov && ov.TryGetValue<int>(out var o) && o >= 1) { order = o; orders[i] = o; }
                else errors.Add(new($"{f}.order", "Must be an integer greater than or equal to 1"));
            }

            result.Add(new EffectiveSigner(i, externalId ?? $"signer-{i + 1}", name ?? "", doc ?? "", email, phone, type, order, channels));
        }

        if (anyOrder)
        {
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is JsonObject so && so["order"] is null)
                    errors.Add(new($"signers[{i}].order", "Required when other signers inform order"));
            var distinct = orders.Where(o => o is not null).Select(o => o!.Value).Distinct().OrderBy(x => x).ToList();
            var missing = Enumerable.Range(1, distinct.Count == 0 ? 0 : distinct[^1]).FirstOrDefault(v => !distinct.Contains(v));
            if (missing != 0)
            {
                var at = Array.FindIndex(orders, o => o is not null && o > missing);
                errors.Add(new($"signers[{at}].order", $"Order has a gap: no signer has order {missing}"));
            }
        }
        return (result, errors);
    }

    private static List<string> ParseChannels(JsonNode? node, string field, List<FieldError> errors)
    {
        var list = new List<string>();
        if (node is not JsonArray a) { errors.Add(new(field, "Must be a list of channels")); return list; }
        for (var i = 0; i < a.Count; i++)
        {
            var c = UpperStr(a[i]);
            if (c is null || !ChannelNames.Contains(c)) errors.Add(new($"{field}[{i}]", $"Must be one of {string.Join(", ", ChannelNames)}"));
            else if (list.Contains(c)) errors.Add(new($"{field}[{i}]", "Duplicate channel"));
            else list.Add(c);
        }
        return list;
    }

    /// <summary>Highest assurance among the effective types (stored as the process-level type when none is informed).</summary>
    public static string HighestType(IEnumerable<string> types) =>
        types.OrderByDescending(t => Array.IndexOf(SignatureTypes, t)).FirstOrDefault() ?? "ADVANCED";

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string? UpperStr(JsonNode? n) => Str(n)?.Trim().ToUpperInvariant();
}
