using System.Diagnostics;
using Orchestrator.Application;
using Orchestrator.Application.Providers;

namespace Orchestrator.Infrastructure.Providers;

/// <summary>Decorator measuring latency and errors of every provider call (metric tags: provider, call).</summary>
public sealed class InstrumentedProviderAdapter(IProviderAdapter inner) : IProviderAdapter
{
    public string Code => inner.Code;

    private async Task<T> Run<T>(string call, Func<Task<T>> action)
    {
        var start = Stopwatch.GetTimestamp();
        using var activity = Telemetry.Source.StartActivity("provider." + call, ActivityKind.Client);
        activity?.SetTag("provider.id", Code);
        try { return await action(); }
        catch (Exception ex)
        {
            Telemetry.ProviderErrors.Add(1, new("provider", Code), new("call", call), new("transient", ex is OperationException { Transient: true }));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            Telemetry.ProviderLatency.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, new KeyValuePair<string, object?>("provider", Code), new("call", call));
        }
    }

    public Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest request, CancellationToken ct) =>
        Run("create_process", () => inner.CreateProcessAsync(request, ct));
    public Task SendDocumentAsync(string externalReference, string fileName, CancellationToken ct) =>
        Run("send_document", async () => { await inner.SendDocumentAsync(externalReference, fileName, ct); return 0; });
    public Task AddSignerAsync(string externalReference, ProviderSigner signer, CancellationToken ct) =>
        Run("add_signer", async () => { await inner.AddSignerAsync(externalReference, signer, ct); return 0; });
    public Task<bool> SupportsPerSignerReleaseAsync(string externalReference, CancellationToken ct) => inner.SupportsPerSignerReleaseAsync(externalReference, ct);
    public Task ReleaseSignerAsync(string externalReference, int position, CancellationToken ct) =>
        Run("release_signer", async () => { await inner.ReleaseSignerAsync(externalReference, position, ct); return 0; });
    public Task<ProviderStatusResult> GetStatusAsync(string externalReference, CancellationToken ct) =>
        Run("get_status", () => inner.GetStatusAsync(externalReference, ct));
    public Task CancelAsync(string externalReference, CancellationToken ct) =>
        Run("cancel", async () => { await inner.CancelAsync(externalReference, ct); return 0; });
    public Task<ProviderFile> DownloadSignedDocumentAsync(string externalReference, byte[] originalContent, string originalContentType, CancellationToken ct) =>
        Run("download_signed", () => inner.DownloadSignedDocumentAsync(externalReference, originalContent, originalContentType, ct));
    public Task<ProviderFile> DownloadEvidenceAsync(string externalReference, CancellationToken ct) =>
        Run("download_evidence", () => inner.DownloadEvidenceAsync(externalReference, ct));
}
