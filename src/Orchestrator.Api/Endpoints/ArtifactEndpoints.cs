using System.Text.Json;
using Orchestrator.Application.Artifacts;

namespace Orchestrator.Api;

public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/signature-processes/{id}/artifacts", async (string id, ArtifactQueries q, CancellationToken ct) =>
            Results.Ok(new { items = await q.ListAsync(id, ct) }));

        app.MapPost("/v1/signature-processes/{id}/download-link", async (string id, HttpContext ctx, DownloadService svc, CancellationToken ct) =>
        {
            DownloadLinkRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    req = await JsonSerializer.DeserializeAsync<DownloadLinkRequest>(ctx.Request.Body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
                }
                catch (JsonException)
                {
                    throw new Orchestrator.Application.Processes.ValidationException([new("body", "Invalid JSON")]);
                }
            }
            var link = await svc.IssueLinkAsync(id, req, ctx.CorrelationId(), ct);
            return Results.Ok(new { url = link.Url, expiresAt = link.ExpiresAt });
        });

        // Authorized by the signed, expiring URL (no permanent credentials, no object key in the URL).
        app.MapGet("/v1/downloads/{artifactId}", async (string artifactId, long? expires, string? sig, HttpContext ctx,
            DownloadService svc, CancellationToken ct) =>
        {
            var opened = await svc.OpenAsync(artifactId, expires ?? 0, sig, ctx.CorrelationId(), ct);
            await using var content = opened.Content;
            ctx.Response.ContentType = opened.Artifact.ContentType;
            ctx.Response.ContentLength = opened.Artifact.Size;
            ctx.Response.Headers["X-Content-SHA256"] = opened.Artifact.Sha256;
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{opened.Artifact.FileName.Replace("\"", "")}\"";
            await content.Content.CopyToAsync(ctx.Response.Body, ct);
        });
    }
}
