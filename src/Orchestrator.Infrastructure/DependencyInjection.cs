using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Reconciliation;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Callbacks;
using Microsoft.Extensions.Options;
using Orchestrator.Infrastructure.Storage;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Workflow;
using Orchestrator.Application.Resilience;
using Orchestrator.Infrastructure.Messaging;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers;

namespace Orchestrator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorCore(this IServiceCollection s, IConfiguration cfg)
    {
        s.AddDbContext<OrchestratorDbContext>(o => o
            .UseNpgsql(cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required"))
            .UseSnakeCaseNamingConvention());
        s.AddScoped<IOrchestratorDb>(sp => sp.GetRequiredService<OrchestratorDbContext>());
        s.AddSingleton<IClock, SystemClock>();
        s.AddScoped<Orchestrator.Application.Abstractions.ActorContext>();
        s.Configure<Orchestrator.Application.Abstractions.SlaOptions>(cfg.GetSection("Sla"));
        s.AddScoped<EventRecorder>();
        s.AddScoped<FakeProviderAdapter>();
        s.Configure<Providers.Real.ProviderOptions>(cfg.GetSection("Provider"));
        s.AddSingleton(sp => new Providers.Real.ProviderSelection { Default = Providers.Real.ProviderCodes.Normalize(sp.GetRequiredService<IConfiguration>()["Provider:Default"]) });
        s.AddSingleton(sp => Providers.Real.DocuSignOptions.From(sp.GetRequiredService<IConfiguration>()));
        s.AddSingleton(sp => Providers.Real.LacunaOptions.From(sp.GetRequiredService<IConfiguration>()));
        s.AddSingleton<Providers.Real.DocuSignTokenProvider>();
        s.AddHttpClient("docusign", c => c.Timeout = TimeSpan.FromSeconds(60));
        s.AddHttpClient("lacuna", c => c.Timeout = TimeSpan.FromSeconds(60));
        s.AddScoped<Providers.Real.OriginalDocumentReader>();
        s.AddScoped<Providers.Real.DocuSignAdapter>();
        s.AddScoped<Providers.Real.LacunaAdapter>();
        s.AddSingleton<IProviderWebhookHandler, Providers.Real.DocuSignWebhookHandler>();
        s.AddSingleton<IProviderWebhookHandler, Providers.Real.LacunaWebhookHandler>();
        s.AddScoped<Providers.Real.ProviderRouter>();
        s.AddScoped<IProviderAdapter>(sp => new InstrumentedProviderAdapter(sp.GetRequiredService<Providers.Real.ProviderRouter>()));
        s.AddHostedService<DeadLetterGaugeService>();
        s.Configure<FakeProviderOptions>(cfg.GetSection("FakeProvider"));
        s.Configure<WorkflowOptions>(cfg.GetSection("Workflow"));
        s.Configure<IdempotencyOptions>(cfg.GetSection("Idempotency"));
        s.Configure<RabbitMqOptions>(cfg.GetSection("RabbitMq"));
        s.AddScoped<CreateProcessHandler>();
        s.AddScoped<CancelProcessHandler>();
        s.AddScoped<ProcessQueries>();
        s.AddScoped<WorkflowEngine>();
        s.Configure<RetryOptions>(cfg.GetSection("Retry"));
        s.AddSingleton<RetryPolicy>();
        s.AddScoped<ReprocessHandler>();
        s.AddScoped<DeadLetterQueries>();
        s.Configure<ArtifactOptions>(cfg.GetSection("Artifacts"));
        s.Configure<S3Options>(cfg.GetSection("S3"));
        s.AddSingleton<IArtifactStore, S3ArtifactStore>();
        s.AddHttpClient<IDocumentFetcher, HttpDocumentFetcher>((sp, c) =>
                c.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ArtifactOptions>>().Value.FetchTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var block = cfg.GetValue<bool>("Artifacts:BlockPrivateNetworks");
                var guard = new SsrfGuard(new SsrfPolicy(AllowPrivateNetworks: !block, AllowHttp: true, AllowedHosts: null));
                return block ? GuardedConnect.CreateHandler(guard, maxRedirects: 3) : new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 };
            });
        s.AddScoped<ArtifactService>();
        s.AddScoped<ArtifactQueries>();
        s.AddScoped<DownloadService>();
        s.AddSingleton<DownloadLinkSigner>();
        s.Configure<CallbackOptions>(cfg.GetSection("Callbacks"));
        s.AddSingleton<SsrfGuard>();
        s.AddSingleton<HostRateLimiter>();
        s.AddHttpClient<ICallbackSender, HttpCallbackSender>((sp, c) =>
                c.Timeout = Timeout.InfiniteTimeSpan) // timeout is enforced per request by the sender
            .ConfigurePrimaryHttpMessageHandler(sp => GuardedConnect.CreateHandler(sp.GetRequiredService<SsrfGuard>(), maxRedirects: 0));
        s.AddScoped<CallbackEmitter>();
        s.AddScoped<CallbackDispatcher>();
        s.AddScoped<CallbackAdmin>();
        s.AddScoped<CallbackQueries>();
        s.Configure<IdentityOptions>(cfg.GetSection("Identity"));
        s.AddScoped<IIdentityProofingAdapter, FakeIdentityAdapter>();
        s.AddScoped<ProofingCore>();
        s.AddScoped<ProofingService>();
        s.AddScoped<IdentityValidationRunner>();
        s.AddScoped<ProofingQueries>();
        s.AddScoped<ReprocessIdentityHandler>();
        s.AddScoped<EvidencePurger>();
        s.Configure<ReconciliationOptions>(cfg.GetSection("Reconciliation"));
        s.AddScoped<ReconciliationService>();
        s.AddScoped<ReconciliationQueries>();
        s.Configure<Orchestrator.Application.Confirmation.ConfirmationOptions>(cfg.GetSection("Confirmation"));
        s.AddScoped<Orchestrator.Application.Confirmation.IConfirmationNotifier, Confirmation.SimulatedConfirmationNotifier>();
        s.AddScoped<Orchestrator.Application.Confirmation.ConfirmationService>();
        s.AddScoped<Orchestrator.Application.Confirmation.SignerAdvancer>();
        s.AddScoped<DocumentUploadService>();
        return s;
    }
}
