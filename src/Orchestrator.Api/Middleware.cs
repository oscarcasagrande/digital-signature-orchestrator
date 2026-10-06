using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Artifacts;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Serilog.Context;

namespace Orchestrator.Api;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) id = "cor_" + Guid.NewGuid().ToString("N")[..24];
        ctx.Items[ItemKey] = id;
        ctx.Response.Headers[Header] = id;
        using (LogContext.PushProperty("CorrelationId", id)) await next(ctx);
    }
}

public static class HttpContextExtensions
{
    public static string CorrelationId(this HttpContext ctx) => (string)ctx.Items[CorrelationIdMiddleware.ItemKey]!;
}

/// <summary>Maps domain/application exceptions to application/problem+json (RFC 7807).</summary>
public sealed class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex)
        {
            if (ctx.Response.HasStarted) throw;
            var (status, title, errors) = ex switch
            {
                ValidationException v => (400, "Validation failed", v.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray())),
                IdempotencyConflictException => (409, "Idempotency conflict", null),
                InvalidTransitionException => (409, "Invalid state transition", null),
                ConflictException => (409, "Conflict", null),
                Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (409, "Concurrent modification, retry the request", null),
                Orchestrator.Application.Confirmation.CodeRejectedException => (422, "Code does not match", null),
                Orchestrator.Application.Confirmation.CodeExpiredException => (410, "Code expired", null),
                Orchestrator.Application.Confirmation.CodeLockedException => (423, "Code locked", null),
                Orchestrator.Application.Confirmation.RateLimitedException => (429, "Too many requests", null),
                NotFoundException => (404, "Not found", null),
                InvalidLinkException => (403, "Invalid download link", null),
                LinkExpiredException => (410, "Download link expired", null),
                _ => (500, "Internal error", null)
            };
            if (status == 500) log.LogError(ex, "Unhandled exception");
            var pd = new ProblemDetails { Status = status, Title = title, Detail = status == 500 ? null : ex.Message, Type = $"https://httpstatuses.io/{status}" };
            pd.Extensions["correlationId"] = ctx.CorrelationId();
            if (errors is not null) pd.Extensions["errors"] = errors;
            if (ex is Orchestrator.Application.Confirmation.CodeRejectedException cr) pd.Extensions["attemptsRemaining"] = cr.AttemptsRemaining;
            if (ex is Orchestrator.Application.Confirmation.RateLimitedException { RetryAfterSeconds: { } ra }) ctx.Response.Headers.RetryAfter = ra.ToString();
            ctx.Response.StatusCode = status;
            await Results.Json(pd, contentType: "application/problem+json").ExecuteAsync(ctx);
        }
    }
}

public sealed class DbHealthCheck(OrchestratorDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
}

/// <summary>
/// Identifies who performs the request. A valid X-Operator-Id header makes the actor OPERATOR/{id} in the journal;
/// without it the actor stays CONSUMER/api. (Replaced by the token subject once authentication exists.)
/// </summary>
public sealed partial class ActorMiddleware(RequestDelegate next)
{
    public const string Header = "X-Operator-Id";

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._@:-]{1,100}$")]
    private static partial System.Text.RegularExpressions.Regex Valid();

    public async Task InvokeAsync(HttpContext ctx, Orchestrator.Application.Abstractions.ActorContext actor, Orchestrator.Application.Abstractions.CallerContext caller)
    {
        // With authentication the actor comes from the token; the header is ignored.
        var raw = caller.Authenticated ? null : ctx.Request.Headers[Header].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!Valid().IsMatch(raw))
            {
                var pd = new ProblemDetails
                {
                    Status = 400, Title = "Validation failed", Detail = "X-Operator-Id must have 1 to 100 characters from A-Z a-z 0-9 . _ @ : -",
                    Type = "https://httpstatuses.io/400"
                };
                pd.Extensions["correlationId"] = ctx.CorrelationId();
                ctx.Response.StatusCode = 400;
                await Results.Json(pd, contentType: "application/problem+json").ExecuteAsync(ctx);
                return;
            }
            actor.Type = "OPERATOR";
            actor.Id = raw;
        }
        await next(ctx);
    }
}
