using System.Text.Json;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Resilience;
using Orchestrator.Application.Workflow;

namespace Orchestrator.Api;

public static class ResilienceEndpoints
{
    public static void MapResilienceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/signature-processes/{id}/retry", async (string id, HttpContext ctx, ReprocessHandler handler, CancellationToken ct) =>
        {
            ReprocessRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    req = await JsonSerializer.DeserializeAsync<ReprocessRequest>(ctx.Request.Body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
                }
                catch (JsonException)
                {
                    throw new ValidationException([new FieldError("body", "Invalid JSON")]);
                }
            }
            return Results.Ok(await handler.HandleAsync(id, req, ctx.CorrelationId(), ct));
        });

        app.MapGet("/v1/dead-letters", async (DeadLetterQueries q, CancellationToken ct, string? domain = null,
            bool resolved = false, string? processId = null, int page = 1, int pageSize = 50) =>
            Results.Ok(await q.ListAsync(domain, resolved, processId, page, pageSize, ct)));
    }
}
