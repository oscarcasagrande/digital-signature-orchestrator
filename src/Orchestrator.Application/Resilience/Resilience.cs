using System.Data.Common;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Resilience;

/// <summary>Retry settings for one operation type; null values fall back to <see cref="RetryOptions.Default"/> and then to the PRD schedule.</summary>
public sealed class RetryTypeOptions
{
    public int? MaxAttempts { get; set; }
    public double[]? DelaysSeconds { get; set; }
    public double? BaseDelaySeconds { get; set; }
    public double? Multiplier { get; set; }
    public double? MaxDelaySeconds { get; set; }
}

public sealed class RetryOptions
{
    /// <summary>Relative jitter: each delay is multiplied by a uniform factor in [1 - Jitter, 1 + Jitter].</summary>
    public double Jitter { get; set; } = 0.2;
    public int UnknownMaxAttempts { get; set; } = 3;
    public RetryTypeOptions Default { get; set; } = new();
    public Dictionary<string, RetryTypeOptions> Operations { get; set; } = new();
}

/// <summary>Exponential backoff with jitter, configurable per operation type (PRD section 15).</summary>
public sealed class RetryPolicy(IOptions<RetryOptions> options)
{
    /// <summary>PRD default: attempt 1 immediate, then +5s, +30s, +2min, +10min, +30min, +2h, +6h.</summary>
    public static readonly double[] StandardSchedule = [0, 5, 30, 120, 600, 1800, 7200, 21600];
    public const int StandardMaxAttempts = 8;

    private RetryTypeOptions? For(OperationType type) =>
        options.Value.Operations.TryGetValue(type.ToString(), out var o) ? o : null;

    public int MaxAttemptsFor(OperationType type) =>
        Math.Max(1, For(type)?.MaxAttempts ?? options.Value.Default.MaxAttempts ?? StandardMaxAttempts);

    public int UnknownMaxAttempts => Math.Max(1, options.Value.UnknownMaxAttempts);

    /// <summary>Delay before the attempt that follows failed attempt number <paramref name="failedAttempt"/> (1-based).</summary>
    public TimeSpan NextDelay(OperationType type, int failedAttempt, double random01)
    {
        var t = For(type);
        var d = options.Value.Default;
        var schedule = t?.DelaysSeconds ?? d.DelaysSeconds ?? (t?.BaseDelaySeconds is null && d.BaseDelaySeconds is null ? StandardSchedule : null);
        double baseSeconds;
        if (schedule is { Length: > 0 })
            baseSeconds = schedule[Math.Min(Math.Max(failedAttempt, 0), schedule.Length - 1)];
        else
        {
            var b = t?.BaseDelaySeconds ?? d.BaseDelaySeconds ?? 5;
            var m = t?.Multiplier ?? d.Multiplier ?? 2;
            var max = t?.MaxDelaySeconds ?? d.MaxDelaySeconds ?? 21600;
            baseSeconds = Math.Min(max, b * Math.Pow(m, Math.Max(failedAttempt - 1, 0)));
        }
        var jitter = Math.Clamp(options.Value.Jitter, 0, 0.99);
        var factor = 1 + (Math.Clamp(random01, 0, 1) * 2 - 1) * jitter;
        return TimeSpan.FromSeconds(Math.Max(0, baseSeconds * factor));
    }

    public TimeSpan NextDelay(OperationType type, int failedAttempt) => NextDelay(type, failedAttempt, Random.Shared.NextDouble());
}

/// <summary>Maps exceptions to TRANSIENT / PERMANENT / UNKNOWN (PRD section 16).</summary>
public static class ErrorClassifier
{
    public static ErrorClass Classify(Exception ex) => ex switch
    {
        OperationException oe => oe.Transient ? ErrorClass.TRANSIENT : ErrorClass.PERMANENT,
        InvalidTransitionException => ErrorClass.PERMANENT,
        ValidationException => ErrorClass.PERMANENT,
        HttpRequestException or TimeoutException or IOException or TaskCanceledException => ErrorClass.TRANSIENT,
        _ => ErrorClass.UNKNOWN
    };

    public static ErrorClass FromHttpStatus(int status) => status switch
    {
        408 or 429 => ErrorClass.TRANSIENT,
        >= 500 and <= 599 => ErrorClass.TRANSIENT,
        >= 400 and <= 499 => ErrorClass.PERMANENT,
        _ => ErrorClass.UNKNOWN
    };

    /// <summary>Infrastructure failures are not operation failures: they must requeue the message, not consume attempts.</summary>
    public static bool IsInfrastructure(Exception ex) => ex is DbException or DbUpdateException;
}

/// <summary>Integration domains. Each has its own command queue and DLQ (queue name equals domain name).</summary>
public static class OperationDomains
{
    public const string SignatureProvider = "signature-provider";
    public const string Artifact = "artifact";
    public const string IdentityProofing = "identity-proofing";
    public const string Callback = "callback";
    public const string Notification = "notification";

    public static readonly string[] All = [SignatureProvider, Artifact, IdentityProofing, Callback, Notification];

    public static string DomainOf(OperationType type) => type switch
    {
        OperationType.DOCUMENT_DOWNLOAD or OperationType.DOCUMENT_STORE
            or OperationType.SIGNED_DOCUMENT_DOWNLOAD or OperationType.SIGNED_DOCUMENT_STORE => Artifact,
        OperationType.PROVIDER_CREATE_PROCESS or OperationType.PROVIDER_SEND_DOCUMENT
            or OperationType.PROVIDER_STATUS_CHECK => SignatureProvider,
        OperationType.IDENTITY_VALIDATION => IdentityProofing,
        OperationType.CONFIRMATION_SEND or OperationType.CONFIRMATION_VERIFY => Notification,
        OperationType.CALLBACK_SEND or OperationType.OUTPUT_DELIVERY => Callback,
        _ => SignatureProvider
    };

    public static string QueueFor(OperationType type) => DomainOf(type);

    /// <summary>Auxiliary operations never change the operational state of the process (they run beside the main flow).</summary>
    public static bool IsAuxiliary(OperationType type) => type is OperationType.CALLBACK_SEND or OperationType.CONFIRMATION_SEND;
    public static string DlqFor(string domain) => domain + "-dlq";
}

public sealed record DeadLetterDto(string DeadLetterId, string ProcessId, string OperationId, string OperationType, string Domain,
    string Queue, string ErrorClass, string Reason, int Attempts, DateTime CreatedAt, DateTime? ResolvedAt);

public sealed class DeadLetterQueries(IOrchestratorDb db, CallerContext? caller = null)
{
    public async Task<PagedResult<DeadLetterDto>> ListAsync(string? domain, bool resolved, string? processId, int page, int pageSize, CancellationToken ct)
    {
        if (domain is not null && !OperationDomains.All.Contains(domain))
            throw new ValidationException([new FieldError("domain", $"Must be one of {string.Join(", ", OperationDomains.All)}")]);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, ProcessQueries.MaxPageSize);
        var q = db.DeadLetters.AsNoTracking().Where(d => resolved ? d.ResolvedAt != null : d.ResolvedAt == null);
        if (caller?.ClientId is { } clientId)
            q = q.Where(d => db.Processes.Any(p => p.Id == d.ProcessId && p.ClientId == clientId)
                             || db.ProofingSessions.Any(s => s.Id == d.ProcessId && s.ClientId == clientId));
        if (domain is not null) q = q.Where(d => d.Domain == domain);
        if (!string.IsNullOrWhiteSpace(processId)) q = q.Where(d => d.ProcessId == processId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<DeadLetterDto>(rows.Select(d => new DeadLetterDto(d.Id, d.ProcessId, d.OperationId, d.OperationType,
            d.Domain, d.Queue, d.ErrorClass.ToString(), d.Reason, d.Attempts, d.CreatedAt, d.ResolvedAt)).ToList(), page, pageSize, total);
    }
}
