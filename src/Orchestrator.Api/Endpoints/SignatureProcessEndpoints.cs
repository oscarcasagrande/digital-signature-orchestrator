using Orchestrator.Application.Processes;

namespace Orchestrator.Api;

public static class SignatureProcessEndpoints
{
    public static void MapSignatureProcessEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/signature-processes");

        g.MapPost("/", async (HttpContext ctx, CreateProcessHandler handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var result = await handler.HandleAsync(key, body, ctx.CorrelationId(), ct);
            var location = $"/v1/signature-processes/{result.Process.ProcessId}";
            ctx.Response.Headers.Location = location;
            return Results.Json(result.Process, statusCode: result.Created ? 202 : 200);
        }).RequireRateLimiting("create");

        g.MapGet("/", async (ProcessQueries queries, CancellationToken ct, string? status = null, string? operationalStatus = null, string? q = null,
            int page = 1, int pageSize = 50) => Results.Ok(await queries.ListAsync(status, operationalStatus, q, page, pageSize, ct)));

        g.MapGet("/{id}", async (string id, ProcessQueries q, CancellationToken ct) => Results.Ok(await q.GetAsync(id, ct)));
        g.MapGet("/{id}/provider", async (string id, HttpContext ctx, ProcessQueries q, CancellationToken ct) =>
            Results.Ok(await q.ProviderInfoAsync(id, ctx.CorrelationId(), ct)));
        g.MapGet("/{id}/status", async (string id, ProcessQueries q, CancellationToken ct) => Results.Ok(await q.StatusAsync(id, ct)));
        g.MapGet("/{id}/operations", async (string id, ProcessQueries q, CancellationToken ct, int page = 1, int pageSize = 50) =>
            Results.Ok(await q.OperationsAsync(id, page, pageSize, ct)));
        g.MapGet("/{id}/events", async (string id, ProcessQueries q, CancellationToken ct, int page = 1, int pageSize = 50) =>
            Results.Ok(await q.EventsAsync(id, page, pageSize, ct)));

        g.MapPost("/{id}/cancel", async (string id, HttpContext ctx, CancelProcessHandler h, CancellationToken ct) =>
            Results.Ok(await h.HandleAsync(id, ctx.CorrelationId(), ct)));
    }
}
