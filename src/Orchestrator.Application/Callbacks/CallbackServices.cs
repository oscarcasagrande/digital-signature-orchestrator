using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Callbacks;

public sealed class SsrfBlockedException(string message) : Exception(message);

/// <summary>Non-2xx response (or transport failure) while delivering a callback; carries the HTTP status when there was one.</summary>
public sealed class CallbackDeliveryException(string message, bool transient, int? statusCode = null) : OperationException(message, transient)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed record CallbackRequest(string Url, string Body, IReadOnlyDictionary<string, string> Headers);

public interface ICallbackSender
{
    /// <summary>Delivers the request. Returns the 2xx status code or throws an <see cref="OperationException"/> already classified.</summary>
    Task<int> SendAsync(CallbackRequest request, CancellationToken ct);
}

/// <summary>Creates one delivery plus one independent CALLBACK_SEND operation per notifiable status change.</summary>
public sealed class CallbackEmitter(IOrchestratorDb db, EventRecorder recorder, IClock clock, RetryPolicy retry)
{
    public static readonly IReadOnlySet<BusinessStatus> Notifiable = new HashSet<BusinessStatus>
    {
        BusinessStatus.SIGNATURE_IN_PROGRESS, BusinessStatus.PARTIALLY_SIGNED, BusinessStatus.SIGNED, BusinessStatus.COMPLETED,
        BusinessStatus.REJECTED, BusinessStatus.CANCELLED, BusinessStatus.FAILED, BusinessStatus.EXPIRED
    };

    public static string EventTypeFor(BusinessStatus s) => "SIGNATURE_PROCESS." + s;

    public async Task EmitAsync(SignatureProcess p, BusinessStatus status, string correlationId, string? causationId, CancellationToken ct)
    {
        if (p.CallbackJson is null || !Notifiable.Contains(status)) return;
        var destination = DescribeDestination(p.CallbackJson);
        if (destination is null) return;

        var persisted = await db.Operations.CountAsync(o => o.ProcessId == p.Id && o.Type == OperationType.CALLBACK_SEND, ct);
        var pending = db.ChangeTracker.Entries<Operation>().Count(e =>
            e.State == EntityState.Added && e.Entity.ProcessId == p.Id && e.Entity.Type == OperationType.CALLBACK_SEND);
        var now = clock.UtcNow;
        var op = CreateProcessHandler.NewOperation(p.Id, OperationType.CALLBACK_SEND, persisted + pending, now,
            retry.MaxAttemptsFor(OperationType.CALLBACK_SEND));
        var delivery = new CallbackDelivery
        {
            Id = Ids.New("cbd"), ProcessId = p.Id, OperationId = op.Id, EventId = Ids.New("evt"),
            EventType = EventTypeFor(status), ProcessStatus = status.ToString(), Destination = destination,
            Status = DeliveryStatus.PENDING, OccurredAt = now, CreatedAt = now
        };
        op.InputJson = JsonSerializer.Serialize(new { deliveryId = delivery.Id });
        db.Operations.Add(op);
        db.CallbackDeliveries.Add(delivery);
        var ev = recorder.Journal(p.Id, "CALLBACK_REQUESTED", "SYSTEM", "callback-emitter", correlationId, causationId,
            new { deliveryId = delivery.Id, eventType = delivery.EventType, destination });
        recorder.EnqueueOperation(op, correlationId, ev.Id, now);
    }

    /// <summary>The callbackId, or the URL without credentials, query and fragment. Never a secret.</summary>
    public static string? DescribeDestination(string callbackJson)
    {
        using var doc = JsonDocument.Parse(callbackJson);
        if (doc.RootElement.TryGetProperty("callbackId", out var id) && id.GetString() is { Length: > 0 } cid) return cid;
        if (doc.RootElement.TryGetProperty("url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        return null;
    }
}

/// <summary>Executes a CALLBACK_SEND operation: builds the payload, signs it and sends it.</summary>
public sealed class CallbackDispatcher(IOrchestratorDb db, ICallbackSender sender, SsrfGuard guard, DownloadLinkSigner links,
    EventRecorder recorder, IClock clock, IOptions<CallbackOptions> options)
{
    public async Task<string> DispatchAsync(SignatureProcess p, Operation op, string correlationId, string? causationId, CancellationToken ct)
    {
        var delivery = await db.CallbackDeliveries.FirstOrDefaultAsync(d => d.OperationId == op.Id, ct)
                       ?? throw new OperationException("Callback delivery not found for operation", transient: false);

        var (url, secret) = await ResolveDestinationAsync(p, ct);
        var error = guard.ValidateUrl(url);
        if (error is not null) throw new CallbackDeliveryException($"Destination rejected by policy: {error}", transient: false);

        string body = await BuildBodyAsync(p, delivery, ct);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headers = CallbackSigner.Headers(secret, delivery.EventId, ts, body);

        var code = await sender.SendAsync(new CallbackRequest(url, body, headers), ct);

        delivery.Attempts = op.Attempt;
        delivery.LastStatusCode = code;
        delivery.LastError = null;
        delivery.Status = DeliveryStatus.DELIVERED;
        Telemetry.CallbackDeliveries.Add(1, new KeyValuePair<string, object?>("result", "delivered"));
        delivery.DeliveredAt = clock.UtcNow;
        var ev = recorder.Journal(p.Id, "CALLBACK_DELIVERED", "SYSTEM", "callback-dispatcher", correlationId, causationId,
            new { deliveryId = delivery.Id, eventId = delivery.EventId, statusCode = code, attempts = op.Attempt });
        return ev.Id;
    }

    private async Task<(string Url, string Secret)> ResolveDestinationAsync(SignatureProcess p, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(p.CallbackJson ?? "{}");
        if (doc.RootElement.TryGetProperty("callbackId", out var idEl) && idEl.GetString() is { Length: > 0 } id)
        {
            var reg = await db.CallbackRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.CallbackId == id, ct);
            if (reg is null || !reg.Active)
                throw new CallbackDeliveryException($"Callback {id} is not registered or is inactive", transient: false);
            return (reg.Url, reg.Secret);
        }
        if (doc.RootElement.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
            return (url, options.Value.DefaultSecret);
        throw new CallbackDeliveryException("Process has no callback destination", transient: false);
    }

    private async Task<string> BuildBodyAsync(SignatureProcess p, CallbackDelivery d, CancellationToken ct)
    {
        object? document = null;
        if (d.ProcessStatus == nameof(BusinessStatus.COMPLETED))
        {
            var signed = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ProcessId == p.Id && a.Type == ArtifactType.SIGNED_DOCUMENT, ct);
            if (signed is not null) document = new { id = signed.Id, downloadUrl = links.Issue(signed.Id).Url };
        }
        return JsonSerializer.Serialize(new
        {
            eventId = d.EventId, eventType = d.EventType, processId = p.Id, externalId = p.ExternalId,
            status = d.ProcessStatus, occurredAt = d.OccurredAt, document
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}

public sealed record CallbackRegistrationDto(string CallbackId, string Url, bool Active, string? Description, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record CallbackCreatedDto(string CallbackId, string Url, bool Active, string? Description, DateTime CreatedAt, DateTime UpdatedAt, string Secret);
public sealed record RegisterCallbackRequest(string? CallbackId, string? Url, string? Secret, string? Description);

public sealed partial class CallbackAdmin(IOrchestratorDb db, SsrfGuard guard, IClock clock, CallerContext? caller = null)
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex IdPattern();

    public async Task<CallbackCreatedDto> RegisterAsync(RegisterCallbackRequest r, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (r.CallbackId is null || !IdPattern().IsMatch(r.CallbackId)) errors.Add(new("callbackId", "Required; letters, digits, _ . - (max 100)"));
        if (string.IsNullOrWhiteSpace(r.Url)) errors.Add(new("url", "Required"));
        else if (r.Url.Length > 2000) errors.Add(new("url", "Max length is 2000"));
        else if (guard.ValidateUrl(r.Url) is { } urlError) errors.Add(new("url", urlError));
        if (r.Secret is not null && (r.Secret.Length < 16 || r.Secret.Length > 200)) errors.Add(new("secret", "Must have 16 to 200 characters"));
        if (r.Description is { Length: > 300 }) errors.Add(new("description", "Max length is 300"));
        if (errors.Count > 0) throw new ValidationException(errors);

        if (await db.CallbackRegistrations.AnyAsync(c => c.CallbackId == r.CallbackId, ct))
            throw new ConflictException($"Callback {r.CallbackId} already exists");

        var now = clock.UtcNow;
        var secret = r.Secret ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var reg = new CallbackRegistration
        {
            CallbackId = r.CallbackId!, Url = r.Url!, Secret = secret, Active = true, Description = r.Description, ClientId = caller?.ClientId,
            CreatedAt = now, UpdatedAt = now
        };
        db.CallbackRegistrations.Add(reg);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex)) { throw new ConflictException($"Callback {r.CallbackId} already exists"); }
        return new CallbackCreatedDto(reg.CallbackId, reg.Url, reg.Active, reg.Description, reg.CreatedAt, reg.UpdatedAt, secret);
    }

    public async Task<IReadOnlyList<CallbackRegistrationDto>> ListAsync(CancellationToken ct) =>
        (await Scoped(db.CallbackRegistrations.AsNoTracking()).OrderBy(c => c.CallbackId).ToListAsync(ct)).Select(Map).ToList();

    private IQueryable<CallbackRegistration> Scoped(IQueryable<CallbackRegistration> q) =>
        caller?.ClientId is { } clientId ? q.Where(c => c.ClientId == clientId) : q;

    public async Task<CallbackRegistrationDto> GetAsync(string id, CancellationToken ct) =>
        Map(await Scoped(db.CallbackRegistrations.AsNoTracking()).FirstOrDefaultAsync(c => c.CallbackId == id, ct) ?? throw new NotFoundException("Callback"));

    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        var reg = await Scoped(db.CallbackRegistrations).FirstOrDefaultAsync(c => c.CallbackId == id, ct) ?? throw new NotFoundException("Callback");
        reg.Active = false;
        reg.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static CallbackRegistrationDto Map(CallbackRegistration c) =>
        new(c.CallbackId, c.Url, c.Active, c.Description, c.CreatedAt, c.UpdatedAt);
}

public sealed record CallbackDeliveryDto(string DeliveryId, string OperationId, string EventId, string EventType, string ProcessStatus,
    string Destination, string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime OccurredAt, DateTime CreatedAt, DateTime? DeliveredAt);

public sealed class CallbackQueries(IOrchestratorDb db)
{
    public async Task<IReadOnlyList<CallbackDeliveryDto>> ListAsync(string processId, CancellationToken ct)
    {
        if (!await db.Processes.AnyAsync(p => p.Id == processId, ct)) throw new NotFoundException("Process");
        var rows = await db.CallbackDeliveries.AsNoTracking().Where(d => d.ProcessId == processId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return rows.Select(d => new CallbackDeliveryDto(d.Id, d.OperationId, d.EventId, d.EventType, d.ProcessStatus, d.Destination,
            d.Status.ToString(), d.Attempts, d.LastStatusCode, d.LastError, d.OccurredAt, d.CreatedAt, d.DeliveredAt)).ToList();
    }
}
