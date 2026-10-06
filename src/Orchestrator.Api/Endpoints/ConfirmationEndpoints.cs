using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Confirmation;
using Orchestrator.Application.Processes;

namespace Orchestrator.Api;

public sealed record ConfirmRequest(string? Code);

public static class ConfirmationEndpoints
{
    public static void MapConfirmationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}");

        g.MapPost("/confirm", async (string id, string signerId, string channel, ConfirmRequest? body, HttpContext ctx, ConfirmationService svc, CancellationToken ct) =>
            Results.Ok(await svc.VerifyAsync(id, signerId, channel, body?.Code, ctx.CorrelationId(), ct)))
            .WithSummary("Confirms the one-time code of a signer on a channel")
            .WithDescription("Body: {\"code\":\"123456\"}. 200 confirmed; 422 wrong code (attemptsRemaining); 410 expired; 423 locked after too many attempts. Request a new code with /resend.");

        g.MapPost("/resend", async (string id, string signerId, string channel, HttpContext ctx, ConfirmationService svc, CancellationToken ct) =>
            Results.Json(await svc.ResendAsync(id, signerId, channel, ctx.CorrelationId(), ct), statusCode: 202))
            .WithSummary("Requests a new code for a channel")
            .WithDescription("Rate limited: minimum interval between sends and a maximum number of sends per channel (429 with Retry-After).");

        app.MapPost("/v1/document-uploads", async (HttpContext ctx, DocumentUploadService uploads, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
                throw new ValidationException([new FieldError("file", "Send multipart/form-data with a file field named 'file'")]);
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new ValidationException([new FieldError("file", "Required")]);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var up = await uploads.StoreAsync(Path.GetFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, ms.ToArray(), ct);
            return Results.Json(up, statusCode: 201);
        }).WithSummary("Uploads a source document for a later process creation (document.source = {type: UPLOAD, uploadId})");

        // Development only: reads the codes recorded by the simulated notifier.
        app.MapGet("/v1/dev/confirmation-codes/{processId}", async (string processId, IOrchestratorDb db, IOptions<ConfirmationOptions> opt, CancellationToken ct) =>
        {
            if (!opt.Value.ExposeSink) return Results.NotFound();
            var rows = await db.NotificationSink.AsNoTracking().Where(n => n.ProcessId == processId).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
            return Results.Ok(rows.Select(n => new { signerId = n.SignerId, channel = n.Channel, destination = n.Destination, code = n.Code, sentAt = n.CreatedAt }));
        }).ExcludeFromDescription();
    }
}
