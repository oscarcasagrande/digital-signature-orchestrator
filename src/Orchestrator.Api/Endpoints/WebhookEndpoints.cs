using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Reconciliation;

namespace Orchestrator.Api;

public static class WebhookEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// Provider webhooks (public routes, authenticated only by the provider signature). The payload is just a trigger:
    /// the process is reconciled against the provider, which stays the source of truth.
    /// </summary>
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/webhooks/{provider}", async (string provider, HttpContext ctx, IEnumerable<IProviderWebhookHandler> handlers, IOrchestratorDb db,
            ReconciliationService reconciliation, EventRecorder recorder, ILogger<ProviderWebhookLog> log, CancellationToken ct) =>
        {
            var handler = handlers.FirstOrDefault(h => h.ProviderCode.Equals(provider, StringComparison.OrdinalIgnoreCase));
            if (handler is null) return Results.NotFound();
            if (ctx.Request.ContentLength > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var verified = handler.Verify(headers, body);
            if (!verified.Authentic)
            {
                log.LogWarning("Rejected {Provider} webhook: invalid or missing signature", handler.ProviderCode);
                return Results.Json(new { title = "Invalid webhook signature", status = 401 }, statusCode: 401);
            }

            var pp = verified.ProviderProcessId is null ? null
                : await db.ProviderProcesses.AsNoTracking().FirstOrDefaultAsync(p => p.ProviderCode == handler.ProviderCode && p.ProviderProcessId == verified.ProviderProcessId, ct);
            if (pp is null) return Results.Ok(new { status = "ignored" }); // unknown reference: nothing leaks

            var corr = ctx.CorrelationId();
            var eventType = verified.EventType is { Length: > 80 } t ? t[..80] : verified.EventType;
            recorder.Journal(pp.ProcessId, "PROVIDER_WEBHOOK_RECEIVED", "PROVIDER", handler.ProviderCode, corr, null, new { eventType });
            await db.SaveChangesAsync(ct);

            try
            {
                var result = await reconciliation.ReconcileAsync(pp.ProcessId, ReconcileOutcomes.Webhook, corr, ct);
                return Results.Ok(new { status = "processed", outcome = result.Outcome });
            }
            catch (ConflictException) { return Results.Ok(new { status = "ignored" }); } // process already finished
        }).ExcludeFromDescription();
    }
}

/// <summary>Logger category for webhook handling.</summary>
public sealed class ProviderWebhookLog;
