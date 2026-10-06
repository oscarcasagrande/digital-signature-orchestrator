namespace Orchestrator.Application.Security;

public static class SensitiveMasker
{
    /// <summary>Keeps only the last two digits of a phone number.</summary>
    public static string? MaskPhone(string? value) => value is null ? null : MaskDocument(value);

    /// <summary>Keeps the first character of the local part and the domain: m***@example.com.</summary>
    public static string? MaskEmail(string? value)
    {
        if (value is null) return null;
        var at = value.IndexOf('@');
        return at <= 0 ? "***" : value[0] + "***" + value[at..];
    }

    /// <summary>Masks all but the last two digits of a personal document number.</summary>
    public static string MaskDocument(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length <= 2) return new string('*', digits.Length);
        return new string('*', digits.Length - 2) + digits[^2..];
    }
}
