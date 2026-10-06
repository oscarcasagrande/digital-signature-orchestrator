using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Application;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Refreshes the pending dead-letter gauge periodically (metric orchestrator.deadletter.size).</summary>
public sealed class DeadLetterGaugeService(IServiceScopeFactory scopes, ILogger<DeadLetterGaugeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                Telemetry.SetDeadLetterSize(await db.DeadLetters.LongCountAsync(d => d.ResolvedAt == null, stop));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug("Dead-letter gauge not refreshed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { }
        }
    }
}
