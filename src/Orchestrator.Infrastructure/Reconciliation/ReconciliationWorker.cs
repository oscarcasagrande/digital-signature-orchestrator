using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Reconciliation;

namespace Orchestrator.Infrastructure.Reconciliation;

/// <summary>
/// Independent periodic worker (PRD section 33): finds processes waiting for the provider, compares states and corrects
/// divergences. Failures are isolated per process and never stop the loop.
/// </summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptions<ReconciliationOptions> options, ILogger<ReconciliationWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Reconciliation worker is disabled");
            return;
        }
        // Give the API time to apply migrations before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, options.Value.IntervalSeconds)), stop); } catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try { await SweepAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Reconciliation sweep failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, options.Value.IntervalSeconds)), stop); }
            catch (OperationCanceledException) { }
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        IReadOnlyList<string> ids;
        using (var scope = scopes.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().FindCandidatesAsync(ct);

        var corrected = 0;
        foreach (var id in ids)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ReconciliationService>()
                    .ReconcileAsync(id, ReconcileOutcomes.Scheduled, "recon_" + Guid.NewGuid().ToString("N")[..16], ct);
                if (result.Corrected)
                {
                    corrected++;
                    log.LogInformation("Reconciled process {ProcessId}: {From} -> {To} (provider {Provider})",
                        id, result.InternalStatus, result.ResultingStatus, result.ProviderStatus);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Terminal or vanished processes and transient infrastructure errors: try again in the next cycle.
                log.LogDebug(ex, "Reconciliation skipped for process {ProcessId}", id);
            }
        }
        return corrected;
    }
}
