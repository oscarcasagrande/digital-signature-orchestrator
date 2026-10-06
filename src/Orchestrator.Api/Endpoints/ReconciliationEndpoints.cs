using Orchestrator.Application.Reconciliation;

namespace Orchestrator.Api;

public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/signature-processes/{id}/reconcile", async (string id, HttpContext ctx, ReconciliationService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReconcileAsync(id, ReconcileOutcomes.Manual, ctx.CorrelationId(), ct)));

        app.MapGet("/v1/signature-processes/{id}/reconciliations", async (string id, ReconciliationQueries q, CancellationToken ct) =>
            Results.Ok(new { items = await q.ListAsync(id, ct) }));
    }
}
