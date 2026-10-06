// Example callback receiver for local demos and the smoke test: verifies the HMAC signature (including replay
// tolerance), records every event and lets you force failures. Not part of the platform.
using System.Collections.Concurrent;
using Orchestrator.Application.Callbacks;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var secret = Environment.GetEnvironmentVariable("SINK_SECRET") ?? "demo-secret-0123456789";
var events = new ConcurrentQueue<object>();
var forcedStatus = 0;

app.MapPost("/hook", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var ts = req.Headers[CallbackSigner.TimestampHeader].FirstOrDefault();
    var sig = req.Headers[CallbackSigner.SignatureHeader].FirstOrDefault();
    var valid = CallbackSigner.Verify(secret, ts, body, sig, DateTime.UtcNow, TimeSpan.FromMinutes(5));
    events.Enqueue(new
    {
        receivedAt = DateTime.UtcNow,
        eventId = req.Headers[CallbackSigner.EventIdHeader].FirstOrDefault(),
        signatureValid = valid,
        body = System.Text.Json.JsonDocument.Parse(body).RootElement.Clone()
    });
    return forcedStatus != 0 ? Results.StatusCode(forcedStatus) : Results.Ok();
});

app.MapGet("/events", () => Results.Ok(events.ToArray()));
app.MapDelete("/events", () => { events.Clear(); return Results.NoContent(); });
app.MapPost("/mode", (int status) => { forcedStatus = status; return Results.Ok(new { forcedStatus }); }); // 0 = healthy
app.MapGet("/health", () => "ok");

app.Run();
