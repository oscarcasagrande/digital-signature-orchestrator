using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Messaging;
using Serilog;

Orchestrator.Infrastructure.Providers.Real.DotEnv.Load(); // credentials come from environment variables / the git-ignored .env
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((_, lc) => lc.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext()
    .WithOrchestratorTelemetry(builder.Configuration, "orchestrator-worker")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {TraceId} {Message:lj}{NewLine}{Exception}"));
builder.Services.AddOrchestratorCore(builder.Configuration);
builder.Services.AddOrchestratorTelemetry(builder.Configuration, "orchestrator-worker");
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddOperationConsumers(builder.Configuration);
builder.Services.AddHostedService<Orchestrator.Infrastructure.Identity.EvidenceRetentionService>();
builder.Services.AddHostedService<Orchestrator.Infrastructure.Reconciliation.ReconciliationWorker>();

await builder.Build().RunAsync();
