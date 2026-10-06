using Microsoft.EntityFrameworkCore;
using Orchestrator.Api;
using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Persistence;
using OpenTelemetry.Trace;
using Serilog;

Orchestrator.Infrastructure.Providers.Real.DotEnv.Load(); // credentials come from environment variables / the git-ignored .env
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext()
    .WithOrchestratorTelemetry(ctx.Configuration, "orchestrator-api")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {TraceId} {Message:lj}{NewLine}{Exception}"));

builder.Services.AddOrchestratorCore(builder.Configuration);
builder.Services.AddOrchestratorSecurity(builder.Configuration);
builder.Services.AddOrchestratorTelemetry(builder.Configuration, "orchestrator-api", t => t.AddAspNetCoreInstrumentation(o => o.Filter = c => !c.Request.Path.StartsWithSegments("/health")));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Signature Orchestrator API", Version = "v1" });
    o.OperationFilter<SwaggerExamplesFilter>();
});
builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("postgres");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ProblemDetailsMiddleware>();
if (builder.Configuration.GetValue("Auth:Enabled", false))
{
    app.UseAuthentication();
    app.UseMiddleware<AccessMiddleware>();
}
app.UseRateLimiter();
app.UseMiddleware<ActorMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.MapHealthChecks("/health");
app.MapSignatureProcessEndpoints();
app.MapConfirmationEndpoints();
app.MapArtifactEndpoints();
app.MapResilienceEndpoints();
app.MapCallbackEndpoints();
app.MapProofingEndpoints();
app.MapReconciliationEndpoints();
app.MapWebhookEndpoints();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    await MigrateWithRetryAsync(app);

app.Run();

static async Task MigrateWithRetryAsync(WebApplication app)
{
    for (var i = 1; ; i++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
            return;
        }
        catch (Exception ex) when (i < 30)
        {
            app.Logger.LogWarning("Database not ready ({Message}); retry {Attempt}/30", ex.Message, i);
            await Task.Delay(2000);
        }
    }
}

public partial class Program;
