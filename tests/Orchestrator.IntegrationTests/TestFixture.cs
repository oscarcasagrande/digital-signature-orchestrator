using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using FluentAssertions;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Infrastructure.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>Real PostgreSQL + RabbitMQ (Testcontainers) with the API and the worker services hosted in-process.</summary>
public sealed class TestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private readonly RabbitMqContainer _mq = new RabbitMqBuilder().WithImage("rabbitmq:3.13-alpine").Build();
    private readonly IContainer _minio = new ContainerBuilder().WithImage("bitnamilegacy/minio:2025.5.24")
        .WithEnvironment("MINIO_ROOT_USER", "minioadmin").WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin123")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();
    private WebApplication _origin = default!;
    /// <summary>Base URL of the in-process HTTP server that serves source documents (/doc.pdf, /missing, /big).</summary>
    public string OriginUrl { get; private set; } = "";
    public static readonly byte[] DocBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n" + string.Concat(Enumerable.Repeat("integration test document body\n", 400)));
    /// <summary>URL used by <see cref="Payload"/> as the source document.</summary>
    public static string DocumentUrl { get; private set; } = "http://127.0.0.1:1/doc.pdf";
    /// <summary>Keys of /toggle/{key} documents that currently answer 503 (lets a test fix the cause later).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ToggleDown = new();
    public const string DefaultCallbackSecret = "test-default-callback-secret-0123456789";
    public static readonly System.Collections.Concurrent.ConcurrentQueue<ReceivedCallback> Received = new();
    /// <summary>Per receiver key: secret used to verify signatures (defaults to <see cref="DefaultCallbackSecret"/>).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> CallbackSecrets = new();
    /// <summary>Per receiver key: number of initial requests answered with 503.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> CallbackFailFirst = new();
    /// <summary>Per receiver key: constant status code to answer with (3xx answers with a redirect).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> CallbackStatus = new();
    public string CallbackUrl(string key) => OriginUrl + "/cb/" + key;
    public static List<ReceivedCallback> ReceivedFor(string key) => Received.Where(r => r.Key == key).ToList();
    public WebApplicationFactory<Program> Factory { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;
    public string PgConnection => _pg.GetConnectionString();
    public string RabbitHost => _mq.Hostname;
    public int RabbitPort => _mq.GetMappedPublicPort(5672);

    public async Task InitializeAsync()
    {
        _ = LiveCredentials.Snapshot; // capture real sandbox credentials before the hosts are pointed at the vendor fakes
        await Task.WhenAll(_pg.StartAsync(), _mq.StartAsync(), _minio.StartAsync());
        var ob = WebApplication.CreateBuilder();
        ob.Logging.ClearProviders();
        ob.WebHost.UseUrls("http://127.0.0.1:0");
        _origin = ob.Build();
        _origin.MapGet("/doc.pdf", () => Results.File(DocBytes, "application/pdf"));
        _origin.MapGet("/big", () => Results.File(new byte[2 * 1024 * 1024], "application/octet-stream"));
        _origin.MapGet("/missing", () => Results.NotFound());
        // Callback receiver: verifies the HMAC signature and records every request; behavior is controlled per key.
        _origin.MapPost("/cb/{key}", async (string key, HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var secret = CallbackSecrets.TryGetValue(key, out var s) ? s : DefaultCallbackSecret;
            var valid = Orchestrator.Application.Callbacks.CallbackSigner.Verify(secret, req.Headers["X-Signature-Timestamp"].FirstOrDefault(), body,
                req.Headers["X-Signature-Signature"].FirstOrDefault(), DateTime.UtcNow, TimeSpan.FromMinutes(5));
            Received.Enqueue(new ReceivedCallback(key, body, req.Headers["X-Signature-Event-Id"].FirstOrDefault() ?? "",
                req.Headers["X-Signature-Timestamp"].FirstOrDefault() ?? "", req.Headers["X-Signature-Signature"].FirstOrDefault() ?? "", valid, DateTime.UtcNow));
            if (CallbackFailFirst.TryGetValue(key, out var remaining) && remaining > 0)
            {
                CallbackFailFirst[key] = remaining - 1;
                return Results.StatusCode(503);
            }
            if (CallbackStatus.TryGetValue(key, out var code))
                return code is >= 300 and < 400 ? Results.Redirect("/doc.pdf") : Results.StatusCode(code);
            return Results.Ok();
        });
        var flakyCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        _origin.MapGet("/flaky/{n:int}/{key}", (int n, string key) =>
            flakyCounts.AddOrUpdate(key, 1, (_, c) => c + 1) <= n ? Results.StatusCode(503) : Results.File(DocBytes, "application/pdf"));
        _origin.MapGet("/forbidden", () => Results.StatusCode(403));
        _origin.MapGet("/toggle/{key}", (string key) =>
            ToggleDown.TryGetValue(key, out var code) ? Results.StatusCode(code) : Results.File(DocBytes, "application/pdf"));
        FakeVendors.Map(_origin);
        await _origin.StartAsync();
        OriginUrl = _origin.Urls.First();
        DocumentUrl = OriginUrl + "/doc.pdf";
        // The adapters read credentials from environment variables only: point them at the in-process vendor fakes.
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["DOCUSIGN_INTEGRATION_KEY"] = "test-ik", ["DOCUSIGN_USER_ID"] = "test-user", ["DOCUSIGN_ACCOUNT_ID"] = "ACC",
            ["DOCUSIGN_PRIVATE_KEY"] = FakeVendors.PrivatePem.Replace("\n", "\n"), ["DOCUSIGN_BASE_URI"] = OriginUrl + "/docusign/restapi",
            ["DOCUSIGN_AUTH_SERVER"] = OriginUrl + "/docusign", ["DOCUSIGN_CONNECT_HMAC_KEY"] = FakeVendors.ConnectKey, ["DOCUSIGN_PRIVATE_KEY_FILE"] = "",
            ["LACUNA_API_KEY"] = FakeVendors.LzApiKey, ["LACUNA_BASE_URI"] = OriginUrl + "/lacuna", ["LACUNA_WEBHOOK_SECRET"] = FakeVendors.LacunaSecret
        }) Environment.SetEnvironmentVariable(k, v);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.ConfigureServices(s =>
            {
                s.AddLogging(l => l.AddProvider(new CapturingLoggerProvider()));
                s.AddHostedService<OutboxPublisher>();
                s.AddOperationConsumers(MessagingRegistration.DefaultQueues);
            });
        });
        Client = Factory.CreateClient();
    }

    private void ApplySettings(IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString());
        b.UseSetting("RabbitMq:Host", _mq.Hostname);
        b.UseSetting("RabbitMq:Port", _mq.GetMappedPublicPort(5672).ToString());
        b.UseSetting("RabbitMq:User", "rabbitmq");
        b.UseSetting("RabbitMq:Password", "rabbitmq");
        b.UseSetting("S3:Endpoint", $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}");
        b.UseSetting("S3:AccessKey", "minioadmin");
        b.UseSetting("S3:SecretKey", "minioadmin123");
        b.UseSetting("Artifacts:MaxDocumentBytes", "1048576");
        b.UseSetting("Artifacts:LinkTtlSeconds", "3");
        b.UseSetting("Artifacts:PublicBaseUrl", "http://localhost");
        b.UseSetting("FakeProvider:SignDelaySeconds", "0.5");
        b.UseSetting("Workflow:PollIntervalSeconds", "1");
        b.UseSetting("Retry:Jitter", "0.2");
        b.UseSetting("Retry:UnknownMaxAttempts", "2");
        b.UseSetting("Retry:Default:MaxAttempts", "4");
        b.UseSetting("Retry:Default:DelaysSeconds:0", "0");
        b.UseSetting("Retry:Default:DelaysSeconds:1", "0.3");
        b.UseSetting("Retry:Default:DelaysSeconds:2", "0.3");
        b.UseSetting("Retry:Default:DelaysSeconds:3", "0.3");
        b.UseSetting("Retry:Operations:DOCUMENT_DOWNLOAD:MaxAttempts", "4");
        b.UseSetting("Retry:Operations:DOCUMENT_DOWNLOAD:DelaysSeconds:0", "0");
        b.UseSetting("Retry:Operations:DOCUMENT_DOWNLOAD:DelaysSeconds:1", "2");
        b.UseSetting("Retry:Operations:DOCUMENT_DOWNLOAD:DelaysSeconds:2", "2");
        b.UseSetting("Retry:Operations:DOCUMENT_DOWNLOAD:DelaysSeconds:3", "2");
            b.UseSetting("Callbacks:DefaultSecret", DefaultCallbackSecret);
            b.UseSetting("Callbacks:AllowHttp", "true");
            b.UseSetting("Callbacks:AllowPrivateNetworks", "true");
            b.UseSetting("Callbacks:RateLimitPerSecond", "1000");
            b.UseSetting("Identity:MaxEvidenceBytes", "2048");
            b.UseSetting("Confirmation:ExposeSink", "true");
            b.UseSetting("Confirmation:ResendMinIntervalSeconds", "1");
    }


    /// <summary>
    /// Starts an extra API host running only the reconciliation worker with a fast schedule against the shared database
    /// and broker (the normal worker of the fixture continues whatever the reconciler schedules). Dispose to stop it.
    /// </summary>
    public async Task<IAsyncDisposable> StartReconcilerAsync()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.UseSetting("Reconciliation:IntervalSeconds", "0.3");
            b.UseSetting("Reconciliation:StaleAfterSeconds", "0.5");
            b.ConfigureServices(s => s.AddHostedService<Orchestrator.Infrastructure.Reconciliation.ReconciliationWorker>());
        });
        factory.CreateClient().Dispose(); // starts the host (and its hosted services)
        await Task.CompletedTask;
        return factory;
    }
    private readonly List<WebApplicationFactory<Program>> _extraFactories = new();

    public const string AuthKey = "integration-test-signing-key-0123456789-abcdef";
    public const string AuthIssuer = "http://test-issuer/realms/orchestrator";

    /// <summary>API-only client with authentication enabled (symmetric test key); workers of the main host process its messages.</summary>
    public HttpClient CreateAuthClient(Dictionary<string, string>? overrides = null)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.UseSetting("Auth:Enabled", "true");
            b.UseSetting("Auth:SigningKey", AuthKey);
            b.UseSetting("Auth:ValidIssuer", AuthIssuer);
            b.UseSetting("Auth:Audience", "orchestrator-api");
            foreach (var (k, v) in overrides ?? new()) b.UseSetting(k, v);
        });
        _extraFactories.Add(f);
        return f.CreateClient();
    }

    public WebApplicationFactory<Program> LastExtraFactory => _extraFactories[^1];

    /// <summary>API-only client (no worker services) with settings overridden, e.g. the strict default callback policy.</summary>
    public HttpClient CreateApiClient(Dictionary<string, string> overrides)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            foreach (var (k, v) in overrides) b.UseSetting(k, v);
        });
        _extraFactories.Add(f);
        return f.CreateClient();
    }

    /// <summary>Simulates a broker outage without changing its address (docker pause/unpause).</summary>
    public async Task SetBrokerPausedAsync(bool paused)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", $"{(paused ? "pause" : "unpause")} {_mq.Id}")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        foreach (var f in _extraFactories) await f.DisposeAsync();
        await Factory.DisposeAsync();
        await _pg.DisposeAsync();
        await _mq.DisposeAsync();
        await _minio.DisposeAsync();
        await _origin.DisposeAsync();
    }

    public static string Payload(string externalId = "CONTRACT-1", int signers = 1, string? documentUrl = null, string? callbackJson = null)
    {
        var s = string.Join(",", Enumerable.Range(0, signers).Select(i =>
            "{\"externalId\":\"s" + i + "\",\"name\":\"Signer " + i + "\",\"document\":\"12345678909\"}"));
        return "{\"externalId\":\"" + externalId + "\","
            + "\"document\":{\"fileName\":\"c.pdf\",\"source\":{\"type\":\"URL\",\"url\":\"" + (documentUrl ?? DocumentUrl) + "\"}},"
            + "\"signers\":[" + s + "],"
            + "\"identityProofing\":{\"validations\":[\"FACE_MATCH\",\"LIVENESS\"]},"
            + "\"signature\":{\"type\":\"ADVANCED\"}"
            + (callbackJson is null ? "" : ",\"callback\":" + callbackJson) + "}";
    }

    public string ToggleUrl(string key) => OriginUrl + "/toggle/" + key;

    public async Task<HttpResponseMessage> CreateAsync(string key, string? body = null) =>
        await Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/signature-processes")
        {
            Headers = { { "Idempotency-Key", key } },
            Content = new StringContent(body ?? Payload(), System.Text.Encoding.UTF8, "application/json")
        });

    public async Task<string> CreateProcessAsync(string? externalId = null, int signers = 1, string? documentUrl = null, string? callbackJson = null)
    {
        var r = await CreateAsync(Guid.NewGuid().ToString(), Payload(externalId ?? "C-" + Guid.NewGuid().ToString("N")[..8], signers, documentUrl, callbackJson));
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString()!;
    }

    public async Task<JsonElement> WaitForStatusAsync(string id, string business, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");
            if (last.GetProperty("businessStatus").GetString() == business) return last;
            await Task.Delay(300);
        }
        throw new TimeoutException($"Process {id} did not reach {business}; last={last}");
    }
}

public sealed record ReceivedCallback(string Key, string Body, string EventId, string Timestamp, string Signature, bool SignatureValid, DateTime At)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<TestFixture>;


/// <summary>Collects every formatted log line (with exceptions) of the hosts so tests can prove secrets never reach the logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
    public ILogger CreateLogger(string categoryName) => new Capture(categoryName);
    public void Dispose() { }

    private sealed class Capture(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// Real sandbox credentials as found in the environment / .env BEFORE the fixture points the hosts at the in-process vendor fakes.
/// Live tests use this snapshot and are skipped when it is incomplete.
/// </summary>
public static class LiveCredentials
{
    private static readonly string[] Names =
    [
        "DOCUSIGN_INTEGRATION_KEY", "DOCUSIGN_USER_ID", "DOCUSIGN_ACCOUNT_ID", "DOCUSIGN_PRIVATE_KEY", "DOCUSIGN_PRIVATE_KEY_FILE", "DOCUSIGN_BASE_URI",
        "DOCUSIGN_AUTH_SERVER", "DOCUSIGN_CONNECT_HMAC_KEY", "LACUNA_API_KEY", "LACUNA_BASE_URI", "LACUNA_WEBHOOK_SECRET"
    ];

    public static readonly IReadOnlyDictionary<string, string?> Snapshot = Load();

    private static Dictionary<string, string?> Load()
    {
        Orchestrator.Infrastructure.Providers.Real.DotEnv.Load(AppContext.BaseDirectory);
        return Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
    }

    public static Microsoft.Extensions.Configuration.IConfiguration Configuration() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(Snapshot.Where(kv => kv.Value is not null)!).Build();

    public static bool HasDocuSign => Orchestrator.Infrastructure.Providers.Real.DocuSignOptions.From(Configuration()).Missing().Count == 0;
    public static bool HasLacuna => Orchestrator.Infrastructure.Providers.Real.LacunaOptions.From(Configuration()).Missing().Count == 0;
}

/// <summary>A test that talks to a real sandbox: skipped (not failed) when its credentials are not in the environment or .env.</summary>
public sealed class LiveDocuSignFactAttribute : FactAttribute
{
    public LiveDocuSignFactAttribute()
    {
        if (!LiveCredentials.HasDocuSign) Skip = "DocuSign sandbox credentials not set (see .env.example)";
    }
}

public sealed class LiveLacunaFactAttribute : FactAttribute
{
    public LiveLacunaFactAttribute()
    {
        if (!LiveCredentials.HasLacuna) Skip = "Lacuna sandbox credentials not set (see .env.example)";
    }
}
