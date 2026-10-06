using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Application.Security;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Confirmation;

public sealed class ConfirmationOptions
{
    public int CodeLength { get; set; } = 6;
    public int CodeTtlSeconds { get; set; } = 600;
    /// <summary>Wrong attempts that lock a code.</summary>
    public int MaxAttempts { get; set; } = 5;
    public int ResendMinIntervalSeconds { get; set; } = 30;
    /// <summary>Maximum sends (first send included) per signer and channel.</summary>
    public int MaxSends { get; set; } = 5;
    /// <summary>HMAC key of stored code hashes. The default is for local development only.</summary>
    public string HashKey { get; set; } = "dev-only-confirmation-key-change-me-0123456789";
    /// <summary>Exposes GET /v1/dev/confirmation-codes/{processId} (the simulated notifier sink). Never enable in production.</summary>
    public bool ExposeSink { get; set; }
}

/// <summary>Message handed to the notification capability. Carries the code: it must never be logged or persisted by implementations.</summary>
public sealed record ConfirmationMessage(string ProcessId, string SignerId, string Channel, string Destination, string Code, int Attempt);

/// <summary>
/// Capability "deliver a one-time code through a channel" (EMAIL, SMS, WHATSAPP). Implementations hide the vendor;
/// failures are <see cref="OperationException"/> (transient or permanent) so the operation retry policy applies.
/// </summary>
public interface IConfirmationNotifier
{
    Task SendAsync(ConfirmationMessage message, CancellationToken ct);
}

public sealed class CodeRejectedException(int attemptsRemaining) : Exception("The code does not match")
{
    public int AttemptsRemaining { get; } = attemptsRemaining;
}
public sealed class CodeExpiredException() : Exception("The code expired; request a new one");
public sealed class CodeLockedException() : Exception("Too many wrong attempts; request a new code");
public sealed class RateLimitedException(string message, int? retryAfterSeconds) : Exception(message)
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record ConfirmationResult(string Channel, string Status);
public sealed record ResendResult(string Channel, string OperationId, string Status);

public sealed class ConfirmationService(IOrchestratorDb db, IConfirmationNotifier notifier, EventRecorder recorder, IClock clock,
    IOptions<ConfirmationOptions> options, RetryPolicy retry, ILogger<ConfirmationService> log)
{
    public static string NewCode(int length)
    {
        var chars = new char[Math.Clamp(length, 4, 10)];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(chars);
    }

    public string Hash(string signerId, string channel, string code)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.HashKey));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{signerId}|{channel}|{code}"))).ToLowerInvariant();
    }

    public static string? DestinationOf(Signer s, string channel) => channel == "EMAIL" ? s.Email : s.Phone;

    /// <summary>
    /// Executes a CONFIRMATION_SEND operation: new code, delivery through the notifier, hash and expiry stored.
    /// The code only lives in memory and in the notifier; journal, operation and logs never see it.
    /// </summary>
    public async Task<string?> SendAsync(Operation op, string correlationId, string? causationId, CancellationToken ct)
    {
        var confirmationId = JsonDocument.Parse(op.InputJson ?? "{}").RootElement.GetProperty("confirmationId").GetString()!;
        var c = await db.Confirmations.FirstAsync(x => x.Id == confirmationId, ct);
        if (c.Status == ConfirmationStatus.CONFIRMED) return causationId;
        var signer = await db.Signers.FirstAsync(s => s.Id == c.SignerId, ct);
        var destination = DestinationOf(signer, c.Channel)
                          ?? throw new OperationException($"Signer has no destination for channel {c.Channel}", transient: false);

        var code = NewCode(options.Value.CodeLength);
        await notifier.SendAsync(new ConfirmationMessage(c.ProcessId, c.SignerId, c.Channel, destination, code, op.Attempt), ct);

        var now = clock.UtcNow;
        c.CodeHash = Hash(c.SignerId, c.Channel, code);
        c.ExpiresAt = now.AddSeconds(options.Value.CodeTtlSeconds);
        c.FailedAttempts = 0;
        c.Status = ConfirmationStatus.SENT;
        c.SendCount++;
        c.LastSentAt = now;
        c.Version++;
        var ev = recorder.Journal(c.ProcessId, "CONFIRMATION_SENT", "SYSTEM", "WORKER", correlationId, causationId, new
        {
            signerId = c.SignerId, channel = c.Channel, destination = Mask(c.Channel, destination), expiresAt = c.ExpiresAt, sendCount = c.SendCount
        });
        op.OutputJson = JsonSerializer.Serialize(new { channel = c.Channel, sendCount = c.SendCount, expiresAt = c.ExpiresAt });
        log.LogInformation("Confirmation code sent: process {ProcessId} signer {SignerId} channel {Channel} (send {SendCount})",
            c.ProcessId, c.SignerId, c.Channel, c.SendCount);
        return ev.Id;
    }

    private static string? Mask(string channel, string destination) => channel == "EMAIL" ? SensitiveMasker.MaskEmail(destination) : SensitiveMasker.MaskPhone(destination);

    /// <summary>Verifies a code. Each attempt is recorded as a CONFIRMATION_VERIFY operation and journaled; the code itself never is.</summary>
    public async Task<ConfirmationResult> VerifyAsync(string processId, string signerId, string channel, string? code, string correlationId, CancellationToken ct)
    {
        channel = channel.ToUpperInvariant();
        if (!SignerPlan.ChannelNames.Contains(channel)) throw new NotFoundException("Confirmation");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 12 || !code.All(char.IsDigit))
            throw new ValidationException([new FieldError("code", "Required, digits only")]);

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var (p, c) = await LoadAsync(processId, signerId, channel, ct);
            if (p.IsTerminal) throw new ConflictException($"Process is {p.BusinessStatus}");
            if (c.Status == ConfirmationStatus.CONFIRMED) return new ConfirmationResult(channel, "CONFIRMED");
            if (c.Status is ConfirmationStatus.PLANNED or ConfirmationStatus.SENDING)
                throw new ConflictException("No code has been delivered for this channel yet");
            if (c.Status == ConfirmationStatus.LOCKED) throw new CodeLockedException();

            var now = clock.UtcNow;
            string result;
            Exception? outcome = null;
            if (c.ExpiresAt is { } exp && exp <= now)
            {
                result = "EXPIRED";
                outcome = new CodeExpiredException();
                Journal("CONFIRMATION_CODE_EXPIRED", c, correlationId, new { signerId, channel });
            }
            else if (c.CodeHash is not null && CryptographicOperations.FixedTimeEquals(
                         Encoding.ASCII.GetBytes(c.CodeHash), Encoding.ASCII.GetBytes(Hash(signerId, channel, code))))
            {
                result = "CONFIRMED";
                c.Status = ConfirmationStatus.CONFIRMED;
                c.ConfirmedAt = now;
                c.CodeHash = null;
                Journal("CONFIRMATION_CONFIRMED", c, correlationId, new { signerId, channel });
            }
            else
            {
                c.FailedAttempts++;
                var remaining = Math.Max(0, options.Value.MaxAttempts - c.FailedAttempts);
                if (remaining == 0)
                {
                    result = "LOCKED";
                    c.Status = ConfirmationStatus.LOCKED;
                    c.CodeHash = null;
                    outcome = new CodeLockedException();
                    Journal("CONFIRMATION_LOCKED", c, correlationId, new { signerId, channel, failedAttempts = c.FailedAttempts });
                }
                else
                {
                    result = "MISMATCH";
                    outcome = new CodeRejectedException(remaining);
                    Journal("CONFIRMATION_CODE_REJECTED", c, correlationId, new { signerId, channel, attemptsRemaining = remaining });
                }
            }
            c.Version++;

            var seq = await db.Operations.CountAsync(o => o.ProcessId == processId && o.Type == OperationType.CONFIRMATION_VERIFY, ct);
            var op = Processes.CreateProcessHandler.NewOperation(processId, OperationType.CONFIRMATION_VERIFY, seq, now,
                retry.MaxAttemptsFor(OperationType.CONFIRMATION_VERIFY));
            op.Status = OperationStatus.COMPLETED;
            op.Attempt = 1;
            op.InputJson = JsonSerializer.Serialize(new { signerId, channel });
            op.OutputJson = JsonSerializer.Serialize(new { result });
            db.Operations.Add(op);

            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (attempt < 6 && (ex is DbUpdateConcurrencyException || ex is DbUpdateException && DbErrors.IsUniqueViolation(ex)))
            {
                continue; // someone else touched the confirmation (or took the operation sequence): re-evaluate from the fresh row
            }
            if (outcome is not null) throw outcome;
            return new ConfirmationResult(channel, "CONFIRMED");
        }
    }

    /// <summary>Requests a new code. Rate limited: minimum interval between sends and a ceiling of sends per channel.</summary>
    public async Task<ResendResult> ResendAsync(string processId, string signerId, string channel, string correlationId, CancellationToken ct)
    {
        channel = channel.ToUpperInvariant();
        if (!SignerPlan.ChannelNames.Contains(channel)) throw new NotFoundException("Confirmation");
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var (p, c) = await LoadAsync(processId, signerId, channel, ct);
            if (p.IsTerminal) throw new ConflictException($"Process is {p.BusinessStatus}");
            if (c.Status == ConfirmationStatus.CONFIRMED) throw new ConflictException("This channel is already confirmed");
            if (c.Status == ConfirmationStatus.PLANNED) throw new ConflictException("The signer is not released yet; codes are sent when it is their turn");
            if (c.Status == ConfirmationStatus.SENDING) throw new ConflictException("A code is already being sent");

            var now = clock.UtcNow;
            var o = options.Value;
            if (c.SendCount >= o.MaxSends) throw new RateLimitedException($"Send limit reached ({o.MaxSends} codes per channel)", null);
            if (c.LastSentAt is { } last && last.AddSeconds(o.ResendMinIntervalSeconds) > now)
                throw new RateLimitedException("Wait before requesting a new code", (int)Math.Ceiling((last.AddSeconds(o.ResendMinIntervalSeconds) - now).TotalSeconds));

            c.Status = ConfirmationStatus.SENDING;
            c.Version++;
            var ev = recorder.Journal(processId, "CONFIRMATION_RESEND_REQUESTED", recorder.Api.Type, recorder.Api.Id, correlationId, null,
                new { signerId, channel });
            var op = NewSendOperation(processId, c, await db.Operations.CountAsync(x => x.ProcessId == processId && x.Type == OperationType.CONFIRMATION_SEND, ct), now);
            db.Operations.Add(op);
            recorder.EnqueueOperation(op, correlationId, ev.Id, now);
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (attempt < 6 && (ex is DbUpdateConcurrencyException || ex is DbUpdateException && DbErrors.IsUniqueViolation(ex))) { continue; }
            return new ResendResult(channel, op.Id, "SENDING");
        }
    }

    public Operation NewSendOperation(string processId, SignerConfirmation c, int sequence, DateTime now)
    {
        var op = Processes.CreateProcessHandler.NewOperation(processId, OperationType.CONFIRMATION_SEND, sequence, now,
            retry.MaxAttemptsFor(OperationType.CONFIRMATION_SEND));
        op.InputJson = JsonSerializer.Serialize(new { confirmationId = c.Id, signerId = c.SignerId, channel = c.Channel });
        return op;
    }

    private async Task<(SignatureProcess, SignerConfirmation)> LoadAsync(string processId, string signerId, string channel, CancellationToken ct)
    {
        var p = await db.Processes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == processId, ct) ?? throw new NotFoundException("Process");
        var c = await db.Confirmations.FirstOrDefaultAsync(x => x.ProcessId == processId && x.SignerId == signerId && x.Channel == channel, ct)
                ?? throw new NotFoundException("Confirmation");
        return (p, c);
    }

    private void Journal(string type, SignerConfirmation c, string correlationId, object meta) =>
        recorder.Journal(c.ProcessId, type, recorder.Api.Type, recorder.Api.Id, correlationId, null, meta);
}

/// <summary>
/// Moves signers forward inside PROVIDER_STATUS_CHECK: when a signer's turn arrives (every signer of a lower order
/// signed) it plans the confirmation sends, and once every required channel is confirmed it releases the signer at the provider.
/// </summary>
public sealed class SignerAdvancer(IOrchestratorDb db, IProviderAdapter provider, EventRecorder recorder, IClock clock, ConfirmationService confirmations)
{
    /// <summary>True when the process needs explicit releases (some signer has a confirmation or an order).</summary>
    public static bool IsGated(IEnumerable<Signer> signers) => signers.Any(s => s.Order is not null || s.ConfirmationChannels.Length > 0);

    public static bool IsTheirTurn(Signer s, IReadOnlyCollection<Signer> all) =>
        s.Order is null || all.Where(o => o.Order < s.Order).All(o => o.Signed);

    public async Task<string?> AdvanceAsync(SignatureProcess p, string correlationId, string? causationId, CancellationToken ct)
    {
        if (!IsGated(p.Signers)) return causationId;
        if (!await provider.SupportsPerSignerReleaseAsync(p.Id, ct)) return await AdvanceProcessLevelAsync(p, correlationId, causationId, ct);
        var now = clock.UtcNow;
        var last = causationId;
        var confs = await db.Confirmations.Where(c => c.ProcessId == p.Id).ToListAsync(ct);
        var sendSeq = await db.Operations.CountAsync(o => o.ProcessId == p.Id && o.Type == OperationType.CONFIRMATION_SEND, ct);

        foreach (var s in p.Signers.OrderBy(x => x.Position))
        {
            if (s.Signed || s.ReleasedAt is not null || !IsTheirTurn(s, p.Signers)) continue;
            var mine = confs.Where(c => c.SignerId == s.Id).ToList();
            foreach (var c in mine.Where(c => c.Status == ConfirmationStatus.PLANNED))
            {
                c.Status = ConfirmationStatus.SENDING;
                c.Version++;
                var ev = recorder.Journal(p.Id, "CONFIRMATION_REQUESTED", "SYSTEM", "WORKER", correlationId, last, new { signerId = s.Id, channel = c.Channel });
                var op = confirmations.NewSendOperation(p.Id, c, sendSeq++, now);
                db.Operations.Add(op);
                recorder.EnqueueOperation(op, correlationId, ev.Id, now);
                last = ev.Id;
            }
            if (mine.All(c => c.Status == ConfirmationStatus.CONFIRMED))
            {
                await provider.ReleaseSignerAsync(p.Id, s.Position, ct);
                s.ReleasedAt = now;
                last = recorder.Journal(p.Id, "SIGNER_RELEASED", "SYSTEM", "WORKER", correlationId, last, new { signerId = s.Id, position = s.Position }).Id;
            }
        }
        return last;
    }

    /// <summary>
    /// Providers that cannot release one signer at a time: every signer gets the confirmation codes right away (their turn is
    /// the provider's business), and the document is sent to the provider only after ALL channels of ALL signers are confirmed.
    /// </summary>
    private async Task<string?> AdvanceProcessLevelAsync(SignatureProcess p, string correlationId, string? causationId, CancellationToken ct)
    {
        if (p.Signers.Any(s => s.ReleasedAt is not null)) return causationId; // already sent
        var now = clock.UtcNow;
        var last = causationId;
        var confs = await db.Confirmations.Where(c => c.ProcessId == p.Id).ToListAsync(ct);
        var sendSeq = await db.Operations.CountAsync(o => o.ProcessId == p.Id && o.Type == OperationType.CONFIRMATION_SEND, ct);
        foreach (var c in confs.Where(c => c.Status == ConfirmationStatus.PLANNED))
        {
            c.Status = ConfirmationStatus.SENDING;
            c.Version++;
            var ev = recorder.Journal(p.Id, "CONFIRMATION_REQUESTED", "SYSTEM", "WORKER", correlationId, last, new { signerId = c.SignerId, channel = c.Channel });
            var op = confirmations.NewSendOperation(p.Id, c, sendSeq++, now);
            db.Operations.Add(op);
            recorder.EnqueueOperation(op, correlationId, ev.Id, now);
            last = ev.Id;
        }
        if (!confs.All(c => c.Status == ConfirmationStatus.CONFIRMED)) return last;

        await provider.SendDocumentAsync(p.Id, p.DocumentFileName ?? "document.pdf", ct);
        foreach (var s in p.Signers) s.ReleasedAt = now;
        last = recorder.Journal(p.Id, "SIGNER_NOTIFIED", "PROVIDER", provider.Code, correlationId, last, new { signers = p.Signers.Count }).Id;
        last = recorder.Journal(p.Id, "SIGNATURE_STARTED", "PROVIDER", provider.Code, correlationId, last).Id;
        return last;
    }
}
