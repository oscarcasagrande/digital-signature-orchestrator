using System.Text.Json;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Workflow;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Api;

public static class ProofingEndpoints
{
    public static void MapProofingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/proofing-sessions");

        g.MapPost("/", async (HttpContext ctx, ProofingService svc, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(ctx, ct);
            var key = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var (session, created) = await svc.CreateAsync(key, body, ctx.CorrelationId(), ct);
            ctx.Response.Headers.Location = $"/v1/proofing-sessions/{session.SessionId}";
            return Results.Json(session, statusCode: created ? 202 : 200);
        });

        g.MapGet("/{id}", async (string id, ProofingQueries q, CancellationToken ct) => Results.Ok(await q.GetAsync(id, ct)));
        g.MapGet("/{id}/result", async (string id, ProofingQueries q, CancellationToken ct) => Results.Ok(await q.ResultAsync(id, ct)));
        g.MapGet("/{id}/operations", async (string id, ProofingQueries q, CancellationToken ct, int page = 1, int pageSize = 50) =>
            Results.Ok(await q.OperationsAsync(id, page, pageSize, ct)));
        g.MapGet("/{id}/events", async (string id, ProofingQueries q, CancellationToken ct, int page = 1, int pageSize = 50) =>
            Results.Ok(await q.EventsAsync(id, page, pageSize, ct)));

        g.MapPost("/{id}/documents", async (string id, HttpContext ctx, ProofingService svc, CancellationToken ct) =>
            Results.Json(await svc.ReceiveEvidenceAsync(id, [ArtifactType.DOCUMENT_FRONT, ArtifactType.DOCUMENT_BACK],
                await ReadBodyAsync(ctx, ct), ctx.CorrelationId(), ct), statusCode: 202));

        g.MapPost("/{id}/biometrics", async (string id, HttpContext ctx, ProofingService svc, CancellationToken ct) =>
            Results.Json(await svc.ReceiveEvidenceAsync(id, [ArtifactType.SELFIE],
                await ReadBodyAsync(ctx, ct), ctx.CorrelationId(), ct), statusCode: 202));

        g.MapPost("/{id}/retry", async (string id, HttpContext ctx, ReprocessIdentityHandler handler, CancellationToken ct) =>
        {
            ReprocessRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try { req = await JsonSerializer.DeserializeAsync<ReprocessRequest>(ctx.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
                catch (JsonException) { throw new ValidationException([new FieldError("body", "Invalid JSON")]); }
            }
            return Results.Ok(await handler.HandleAsync(id, req, ctx.CorrelationId(), ct));
        });

        g.MapDelete("/{id}/evidence", async (string id, HttpContext ctx, EvidencePurger purger, CancellationToken ct) =>
        {
            await purger.PurgeAsync(id, "requested", ctx.CorrelationId(), requireCompleted: true, ct);
            return Results.NoContent();
        });
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync(ct);
    }
}
