using System.Text.Json;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Processes;

namespace Orchestrator.Api;

public static class CallbackEndpoints
{
    public static void MapCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/callbacks");

        g.MapPost("/", async (HttpContext ctx, CallbackAdmin admin, CancellationToken ct) =>
        {
            RegisterCallbackRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<RegisterCallbackRequest>(ctx.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            }
            catch (JsonException)
            {
                throw new ValidationException([new FieldError("body", "Invalid JSON")]);
            }
            var created = await admin.RegisterAsync(req ?? new RegisterCallbackRequest(null, null, null, null), ct);
            return Results.Created($"/v1/callbacks/{created.CallbackId}", created);
        });

        g.MapGet("/", async (CallbackAdmin admin, CancellationToken ct) => Results.Ok(new { items = await admin.ListAsync(ct) }));
        g.MapGet("/{callbackId}", async (string callbackId, CallbackAdmin admin, CancellationToken ct) =>
            Results.Ok(await admin.GetAsync(callbackId, ct)));
        g.MapDelete("/{callbackId}", async (string callbackId, CallbackAdmin admin, CancellationToken ct) =>
        {
            await admin.DeactivateAsync(callbackId, ct);
            return Results.NoContent();
        });

        app.MapGet("/v1/signature-processes/{id}/callbacks", async (string id, CallbackQueries q, CancellationToken ct) =>
            Results.Ok(new { items = await q.ListAsync(id, ct) }));
    }
}
