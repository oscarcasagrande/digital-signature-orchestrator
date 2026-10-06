using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;

namespace Orchestrator.Infrastructure.Providers.Real;

/// <summary>
/// The <see cref="IProviderAdapter"/> the rest of the system sees. New processes go to the configured default provider; every
/// later call is routed to the provider recorded in provider_process, so changing the configuration never breaks a process in flight.
/// Adapters are resolved lazily: a provider that is not selected never needs its credentials.
/// </summary>
public sealed class ProviderRouter(IServiceProvider services, IOrchestratorDb db, ProviderSelection selection) : IProviderAdapter
{
    private string _last = selection.Default;
    public string Code => _last;

    private IProviderAdapter Resolve(string code)
    {
        _last = code;
        return code switch
        {
            ProviderCodes.DocuSign => services.GetRequiredService<DocuSignAdapter>(),
            ProviderCodes.Lacuna => services.GetRequiredService<LacunaAdapter>(),
            _ => services.GetRequiredService<FakeProviderAdapter>()
        };
    }

    private async Task<IProviderAdapter> ForAsync(string externalReference, CancellationToken ct)
    {
        var code = await db.ProviderProcesses.AsNoTracking().Where(p => p.ExternalReference == externalReference)
            .Select(p => p.ProviderCode).FirstOrDefaultAsync(ct);
        return Resolve(code ?? selection.Default);
    }

    // A retried create stays with the provider it started with (the row exists once the first attempt was saved).
    public async Task<ProviderProcessInfo> CreateProcessAsync(CreateProviderProcessRequest request, CancellationToken ct) =>
        await (await ForAsync(request.ExternalReference, ct)).CreateProcessAsync(request, ct);


    public async Task SendDocumentAsync(string r, string f, CancellationToken ct) => await (await ForAsync(r, ct)).SendDocumentAsync(r, f, ct);
    public async Task AddSignerAsync(string r, ProviderSigner s, CancellationToken ct) => await (await ForAsync(r, ct)).AddSignerAsync(r, s, ct);
    public async Task ReleaseSignerAsync(string r, int position, CancellationToken ct) => await (await ForAsync(r, ct)).ReleaseSignerAsync(r, position, ct);
    public async Task<bool> SupportsPerSignerReleaseAsync(string r, CancellationToken ct) => await (await ForAsync(r, ct)).SupportsPerSignerReleaseAsync(r, ct);
    public async Task<ProviderStatusResult> GetStatusAsync(string r, CancellationToken ct) => await (await ForAsync(r, ct)).GetStatusAsync(r, ct);
    public async Task CancelAsync(string r, CancellationToken ct) => await (await ForAsync(r, ct)).CancelAsync(r, ct);
    public async Task<ProviderFile> DownloadSignedDocumentAsync(string r, byte[] o, string c, CancellationToken ct) => await (await ForAsync(r, ct)).DownloadSignedDocumentAsync(r, o, c, ct);
    public async Task<ProviderFile> DownloadEvidenceAsync(string r, CancellationToken ct) => await (await ForAsync(r, ct)).DownloadEvidenceAsync(r, ct);
}
