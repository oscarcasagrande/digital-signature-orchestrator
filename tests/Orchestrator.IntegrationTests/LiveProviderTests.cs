using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers.Real;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>
/// OPTIONAL tests against the real DocuSign (demo) and Lacuna Signer sandboxes. They use the credentials found in the environment
/// or in .env BEFORE the fixture redirects the hosts to the in-process fakes, and they are SKIPPED when those are missing.
/// They create one throw-away envelope/document, check its status and cancel it; nobody has to sign anything.
/// </summary>
[Collection("integration")]
public class LiveProviderTests(TestFixture f)
{
    // A tiny but valid single-page PDF.
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
        + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n"
        + "4 0 obj<</Length 44>>stream\nBT /F1 18 Tf 72 700 Td (Sandbox test) Tj ET\nendstream endobj\n"
        + "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R/Size 6>>\n%%EOF\n");

    private async Task<string> SeedProcessAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var id = Ids.New("sig");
        var now = DateTime.UtcNow;
        db.Processes.Add(new SignatureProcess
        {
            Id = id, ExternalId = "LIVE-" + id, BusinessStatus = BusinessStatus.VALIDATING, SignatureType = "ADVANCED", CorrelationId = "live",
            CreatedAt = now, UpdatedAt = now, RequestJson = "{}"
        });
        await scope.ServiceProvider.GetRequiredService<ArtifactService>().StoreAsync(id, ArtifactType.ORIGINAL_DOCUMENT, Pdf, "application/pdf", "sandbox-test.pdf", default);
        await db.SaveChangesAsync();
        return id;
    }

    private static CreateProviderProcessRequest Request(string id) =>
        new(id, "LIVE-" + id, "ADVANCED", [new ProviderSigner(0, "Sandbox Tester", Environment.GetEnvironmentVariable("SANDBOX_SIGNER_EMAIL") ?? "sandbox.signer@example.com", "12345678909")]);

    [LiveDocuSignFact]
    public async Task DocuSign_demo_create_send_status_and_void()
    {
        var options = DocuSignOptions.From(LiveCredentials.Configuration());
        using var scope = f.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var http = sp.GetRequiredService<IHttpClientFactory>();
        var clock = sp.GetRequiredService<IClock>();
        var adapter = new DocuSignAdapter(sp.GetRequiredService<IOrchestratorDb>(), http, options, new DocuSignTokenProvider(options, http, clock),
            sp.GetRequiredService<OriginalDocumentReader>(), clock);
        var id = await SeedProcessAsync(scope);

        var info = await adapter.CreateProcessAsync(Request(id), default);
        await sp.GetRequiredService<OrchestratorDbContext>().SaveChangesAsync();
        info.ProviderProcessId.Should().NotBeNullOrEmpty();
        (await adapter.CreateProcessAsync(Request(id), default)).ProviderProcessId.Should().Be(info.ProviderProcessId, "creation is idempotent");

        await adapter.SendDocumentAsync(id, "sandbox-test.pdf", default);
        var status = await adapter.GetStatusAsync(id, default);
        status.NormalizedStatus.Should().Be(ProviderStatuses.Pending);
        status.SignedPositions.Should().BeEmpty();

        await adapter.CancelAsync(id, default);
        (await adapter.GetStatusAsync(id, default)).NormalizedStatus.Should().Be(ProviderStatuses.Cancelled);
    }

    [LiveLacunaFact]
    public async Task Lacuna_demo_create_send_status_and_cancel()
    {
        var options = LacunaOptions.From(LiveCredentials.Configuration());
        using var scope = f.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var adapter = new LacunaAdapter(sp.GetRequiredService<IOrchestratorDb>(), sp.GetRequiredService<IHttpClientFactory>(), options,
            sp.GetRequiredService<OriginalDocumentReader>(), sp.GetRequiredService<IClock>());
        var id = await SeedProcessAsync(scope);

        await adapter.CreateProcessAsync(Request(id), default);
        await sp.GetRequiredService<OrchestratorDbContext>().SaveChangesAsync();
        await adapter.SendDocumentAsync(id, "sandbox-test.pdf", default);
        await sp.GetRequiredService<OrchestratorDbContext>().SaveChangesAsync();
        var status = await adapter.GetStatusAsync(id, default);
        status.NormalizedStatus.Should().Be(ProviderStatuses.Pending);

        await adapter.CancelAsync(id, default);
        (await adapter.GetStatusAsync(id, default)).NormalizedStatus.Should().Be(ProviderStatuses.Cancelled);
    }
}
