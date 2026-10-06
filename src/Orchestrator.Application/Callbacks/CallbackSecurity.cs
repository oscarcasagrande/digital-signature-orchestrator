using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Orchestrator.Application.Callbacks;

public sealed class CallbackOptions
{
    /// <summary>Secret used to sign callbacks sent to dynamic URLs. The default is for local development only.</summary>
    public string DefaultSecret { get; set; } = "dev-only-callback-secret-change-me";
    public bool AllowPrivateNetworks { get; set; }
    public bool AllowHttp { get; set; }
    public string[]? AllowedHosts { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int RateLimitPerSecond { get; set; } = 10;
    public int SignatureToleranceSeconds { get; set; } = 300;
}

/// <summary>HMAC-SHA256 callback signatures with replay protection (PRD section 7).</summary>
public static class CallbackSigner
{
    public const string EventIdHeader = "X-Signature-Event-Id";
    public const string TimestampHeader = "X-Signature-Timestamp";
    public const string SignatureHeader = "X-Signature-Signature";

    public static string Sign(string secret, long timestamp, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static IReadOnlyDictionary<string, string> Headers(string secret, string eventId, long timestamp, string body) =>
        new Dictionary<string, string>
        {
            [EventIdHeader] = eventId,
            [TimestampHeader] = timestamp.ToString(),
            [SignatureHeader] = Sign(secret, timestamp, body)
        };

    /// <summary>Receiver-side check: constant-time comparison plus timestamp tolerance (replay protection).</summary>
    public static bool Verify(string secret, string? timestampHeader, string body, string? signatureHeader, DateTime nowUtc, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader) || !long.TryParse(timestampHeader, out var ts)) return false;
        var age = nowUtc - DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        if (age.Duration() > tolerance) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts, body));
        var given = Encoding.ASCII.GetBytes(signatureHeader);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}

public sealed record SsrfPolicy(bool AllowPrivateNetworks, bool AllowHttp, string[]? AllowedHosts);

/// <summary>Egress policy: which destinations the platform may call (PRD section 8, constitution security constraints).</summary>
public sealed class SsrfGuard(SsrfPolicy policy)
{
    public SsrfGuard(IOptions<CallbackOptions> o)
        : this(new SsrfPolicy(o.Value.AllowPrivateNetworks, o.Value.AllowHttp, o.Value.AllowedHosts)) { }

    public SsrfPolicy Policy => policy;

    /// <summary>Static checks on a URL (no DNS). Returns an error message or null when acceptable.</summary>
    public string? ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Must be an absolute URL";
        if (u.Scheme != Uri.UriSchemeHttps && !(policy.AllowHttp && u.Scheme == Uri.UriSchemeHttp))
            return "Must be an https URL";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Credentials in the URL are not allowed";
        var host = u.IdnHost;
        if (policy.AllowedHosts is { Length: > 0 } && !HostAllowed(host)) return "Host is not in the allowlist";
        if (policy.AllowPrivateNetworks) return null;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Local destinations are not allowed";
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsBlocked(ip)) return "Private or reserved addresses are not allowed";
        return null;
    }

    public bool HostAllowed(string host)
    {
        if (policy.AllowedHosts is not { Length: > 0 } list) return true;
        foreach (var pattern in list)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>True when the resolved address must not be contacted under the current policy.</summary>
    public bool IsBlocked(IPAddress ip) => !policy.AllowPrivateNetworks && IsPrivateOrReserved(ip);

    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127
                   || (b[0] == 100 && b[1] is >= 64 and <= 127)       // CGNAT 100.64/10
                   || (b[0] == 169 && b[1] == 254)                    // link-local, cloud metadata
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // IETF protocol assignments
                   || (b[0] == 198 && b[1] is 18 or 19)               // benchmarking
                   || b[0] >= 224;                                    // multicast and reserved
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                   || (b[0] & 0xFE) == 0xFC;                          // unique local fc00::/7
        }
        return true;
    }
}

/// <summary>Sliding-window limiter per destination host (in memory, per instance).</summary>
public sealed class HostRateLimiter(IOptions<CallbackOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string host) => TryAcquire(host, Environment.TickCount64);

    public bool TryAcquire(string host, long nowMs)
    {
        var limit = Math.Max(1, options.Value.RateLimitPerSecond);
        var q = _hits.GetOrAdd(host, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && nowMs - q.Peek() >= 1000) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(nowMs);
            return true;
        }
    }
}
