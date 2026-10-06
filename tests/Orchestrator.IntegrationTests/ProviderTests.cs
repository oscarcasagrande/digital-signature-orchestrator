using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers.Real;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>DocuSign and Lacuna adapters, router, webhooks and reconciliation against the in-process vendor fakes (no credentials needed).</summary>
[Collection("integration")]
public class ProviderTests(TestFixture f)
{
    private const string Cpf = "12345678909";

    private static string Signer(string name, string email, int? order = null) =>
        "{\"name\":\"" + name + "\",\"document\":\"" + Cpf + "\",\"email\":\"" + email + "\"" + (order is null ? "" : ",\"order\":" + order) + "}";

    private static string Body(string externalId, string signers, string? defaults = null) =>
        "{\"externalId\":\"" + externalId + "\",\"document\":{\"fileName\":\"contract.pdf\",\"source\":{\"type\":\"URL\",\"url\":\"" + TestFixture.DocumentUrl + "\"}},"
        + "\"defaults\":" + (defaults ?? "{\"signatureType\":\"ADVANCED\"}") + ",\"signers\":[" + signers + "]}";

    private ProviderSelection Selection => f.Factory.Services.GetRequiredService<ProviderSelection>();

    /// <summary>Creates a process while <paramref name="provider"/> is the configured default, and waits until it is registered at the provider.</summary>
    private async Task<(string Id, string Ref)> CreateWithAsync(string provider, string signers, string? defaults = null)
    {
        var reference = "PRV-" + Guid.NewGuid().ToString("N")[..10];
        var previous = Selection.Default;
        Selection.Default = provider;
        try
        {
            var r = await f.CreateAsync(Guid.NewGuid().ToString(), Body(reference, signers, defaults));
            r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
            var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString()!;
            await WaitAsync(async () => (await Detail(id)).GetProperty("provider").ValueKind == JsonValueKind.String, 30, "provider registration");
            return (id, reference);
        }
        finally { Selection.Default = previous; }
    }

    private Task<JsonElement> Detail(string id) => f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(250); }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    private async Task<List<string>> EventTypes(string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=200")).GetProperty("items")
            .EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();

    private async Task<List<JsonElement>> Artifacts(string id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").EnumerateArray().ToList();

    private async Task<byte[]> Download(string processId, JsonElement artifact)
    {
        var link = await f.Client.PostAsJsonAsync($"/v1/signature-processes/{processId}/download-link", new { artifactId = artifact.GetProperty("artifactId").GetString() });
        var url = new Uri((await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!);
        return await f.Client.GetByteArrayAsync(url.PathAndQuery);
    }

    // ---- selection ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_simulated_provider_is_the_default_and_needs_no_credentials()
    {
        Selection.Default.Should().Be("SIMULATED");
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await Detail(id)).GetProperty("provider").GetString().Should().Be("SIMULATED");
        ProviderCodes.Normalize(null).Should().Be("SIMULATED");
        Assert.Throws<InvalidOperationException>(() => ProviderCodes.Normalize("Clicksign"));
    }

    [Fact]
    public async Task A_process_stays_with_the_provider_it_started_with_when_the_default_changes()
    {
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        Selection.Default.Should().Be("SIMULATED"); // back to the default; the process is already at DocuSign
        var env = FakeVendors.DsByRef(id)!;
        await WaitAsync(() => Task.FromResult(env.Status == "sent"), 20, "envelope sent");
        FakeVendors.DsComplete(env.Id);
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await Detail(id)).GetProperty("provider").GetString().Should().Be("DOCUSIGN");
    }

    // ---- DocuSign ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocuSign_end_to_end_creates_one_envelope_signs_in_order_and_stores_signed_document_and_evidence()
    {
        var (id, reference) = await CreateWithAsync("DOCUSIGN", Signer("Maria Souza", "maria@example.com", 1) + "," + Signer("João Lima", "joao@example.com", 2));
        var ext = id;
        var env = FakeVendors.DsByRef(ext)!;
        await WaitAsync(() => Task.FromResult(env.Status == "sent"), 20, "envelope sent");

        FakeVendors.DsAllByRef(ext).Should().ContainSingle("the create is idempotent per process");
        env.Document.Should().Equal(TestFixture.DocBytes);
        env.FileName.Should().Be("contract.pdf");
        env.Signers.Select(s => (s.RecipientId, s.Email, s.RoutingOrder)).Should().Equal(("1", "maria@example.com", "1"), ("2", "joao@example.com", "2"));

        FakeVendors.DsSign(env.Id, 0);
        await f.WaitForStatusAsync(id, "PARTIALLY_SIGNED");
        (await Detail(id)).GetProperty("signers")[0].GetProperty("signed").GetBoolean().Should().BeTrue();

        FakeVendors.DsComplete(env.Id);
        await f.WaitForStatusAsync(id, "COMPLETED");
        var arts = (await Artifacts(id)).ToDictionary(a => a.GetProperty("type").GetString()!);
        arts.Keys.Should().BeEquivalentTo("ORIGINAL_DOCUMENT", "SIGNED_DOCUMENT", "EVIDENCE");
        Encoding.ASCII.GetString(await Download(id, arts["SIGNED_DOCUMENT"])).Should().Contain("%%DOCUSIGN-SIGNED");
        Encoding.ASCII.GetString(await Download(id, arts["EVIDENCE"])).Should().Contain("certificate of completion");
        (await EventTypes(id)).Should().ContainInOrder("PROVIDER_ACCEPTED", "SIGNATURE_STARTED", "SIGNER_SIGNED", "SIGNATURE_COMPLETED", "PROCESS_COMPLETED");
        reference.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DocuSign_authenticates_with_a_valid_jwt_grant_and_reuses_the_token()
    {
        await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        var before = FakeVendors.DsTokenRequests;
        before.Should().BeGreaterThan(0);
        var assertion = FakeVendors.DsAssertions.Last();
        assertion.Split('.').Should().HaveCount(3);
        FakeVendors.DsAuthFailures.Should().Be(0);
        // another process within the token lifetime does not authenticate again
        await CreateWithAsync("DOCUSIGN", Signer("Bia", "bia@example.com"));
        FakeVendors.DsTokenRequests.Should().Be(before);
    }

    [Fact]
    public async Task DocuSign_holds_the_envelope_until_every_signer_confirmed_every_channel()
    {
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com", 1) + "," + Signer("Bia", "bia@example.com", 2),
            defaults: "{\"signatureType\":\"ADVANCED\",\"confirmation\":[\"EMAIL\"]}");
        var ext = id;
        var env = FakeVendors.DsByRef(ext)!;

        // both signers get a code right away (the provider decides the order), but nothing is sent to DocuSign yet
        List<JsonElement> codes = [];
        await WaitAsync(async () =>
        {
            codes = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/dev/confirmation-codes/{id}")).EnumerateArray().ToList();
            return codes.Count == 2;
        }, 30, "two confirmation codes");
        await Task.Delay(2500);
        env.Status.Should().Be("created");
        (await EventTypes(id)).Should().Contain("PROVIDER_SEND_DEFERRED").And.NotContain("SIGNATURE_STARTED");

        var d = await Detail(id);
        async Task Confirm(int i) => (await f.Client.PostAsJsonAsync(
            $"/v1/signature-processes/{id}/signers/{d.GetProperty("signers")[i].GetProperty("id").GetString()}/confirmations/EMAIL/confirm",
            new { code = codes[i].GetProperty("code").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);
        await Confirm(0);
        await Task.Delay(2500);
        env.Status.Should().Be("created", "one signer is still unconfirmed");

        await Confirm(1);
        await WaitAsync(() => Task.FromResult(env.Status == "sent"), 20, "envelope sent after the last confirmation");
        (await EventTypes(id)).Should().Contain(["SIGNER_NOTIFIED", "SIGNATURE_STARTED"]);
        FakeVendors.DsComplete(env.Id);
        await f.WaitForStatusAsync(id, "COMPLETED");
    }

    [Fact]
    public async Task DocuSign_transient_failures_are_retried_without_duplicating_the_envelope_and_permanent_ones_stop_the_operation()
    {
        FakeVendors.Fail("ds:create", 2, 503);
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        var ext = id;
        FakeVendors.DsAllByRef(ext).Should().ContainSingle();
        (await EventTypes(id)).Should().Contain("OPERATION_RETRY_SCHEDULED");

        FakeVendors.Fail("ds:create", 1, 429);
        var (id2, _) = await CreateWithAsync("DOCUSIGN", Signer("Bia", "bia@example.com"));
        FakeVendors.DsAllByRef(id2).Should().ContainSingle();
    }

    [Fact]
    public async Task DocuSign_permanent_rejection_marks_the_operation_failed_and_asks_for_manual_action()
    {
        FakeVendors.Fail("ds:create", 1, 400);
        var reference = "PRV-" + Guid.NewGuid().ToString("N")[..10];
        Selection.Default = "DOCUSIGN";
        string id;
        try
        {
            var r = await f.CreateAsync(Guid.NewGuid().ToString(), Body(reference, Signer("Ana", "ana@example.com")));
            id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString()!;
            await WaitAsync(async () => (await Detail(id)).GetProperty("operationalStatus").GetString() == "MANUAL_ACTION", 30, "manual action");
        }
        finally { Selection.Default = "SIMULATED"; }
        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        var failed = ops.Single(o => o.GetProperty("status").GetString() == "FAILED");
        failed.GetProperty("type").GetString().Should().Be("PROVIDER_CREATE_PROCESS");
        failed.GetProperty("error").GetProperty("errorClass").GetString().Should().Be("PERMANENT");
    }

    [Fact]
    public async Task Missing_credentials_fail_permanently_naming_the_variables_and_never_their_values()
    {
        using var scope = f.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var http = sp.GetRequiredService<IHttpClientFactory>();
        var clock = sp.GetRequiredService<IClock>();
        var empty = new DocuSignOptions(null, null, null, null, "https://demo.docusign.net/restapi", "account-d.docusign.com", null);
        var ds = new DocuSignAdapter(sp.GetRequiredService<IOrchestratorDb>(), http, empty, new DocuSignTokenProvider(empty, http, clock),
            sp.GetRequiredService<OriginalDocumentReader>(), clock);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => ds.CreateProcessAsync(
            new CreateProviderProcessRequest("sig_x", "E", "ADVANCED", [new(0, "Ana", "ana@example.com")]), default));
        ex.Transient.Should().BeFalse();
        ex.Message.Should().Contain("DOCUSIGN_INTEGRATION_KEY").And.Contain("DOCUSIGN_ACCOUNT_ID").And.Contain("DOCUSIGN_PRIVATE_KEY");

        var lz = new LacunaAdapter(sp.GetRequiredService<IOrchestratorDb>(), http, new LacunaOptions(null, LacunaOptions.DefaultBaseUri, null),
            sp.GetRequiredService<OriginalDocumentReader>(), clock);
        (await Assert.ThrowsAsync<ProviderException>(() => lz.CreateProcessAsync(
            new CreateProviderProcessRequest("sig_x", "E", "ADVANCED", [new(0, "Ana", "ana@example.com")]), default))).Message.Should().Contain("LACUNA_API_KEY");
    }

    [Fact]
    public async Task Real_providers_need_an_email_for_every_signer()
    {
        using var scope = f.Factory.Services.CreateScope();
        var ds = scope.ServiceProvider.GetRequiredService<DocuSignAdapter>();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => ds.CreateProcessAsync(
            new CreateProviderProcessRequest("sig_noemail", "E", "ADVANCED", [new(0, "Ana", "ana@example.com"), new(1, "Bia")]), default));
        ex.Transient.Should().BeFalse();
        ex.Message.Should().Contain("signer 2");
    }

    [Fact]
    public async Task Cancelling_a_process_voids_the_envelope_at_DocuSign()
    {
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        var env = FakeVendors.DsByRef(id)!;
        await WaitAsync(() => Task.FromResult(env.Status == "sent"), 20, "envelope sent");
        (await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        env.Status.Should().Be("voided");
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("CANCELLED");
    }

    [Fact]
    public async Task Rejection_at_DocuSign_ends_the_process_as_rejected()
    {
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        var env = FakeVendors.DsByRef(id)!;
        await WaitAsync(() => Task.FromResult(env.Status == "sent"), 20, "envelope sent");
        env.Status = "declined";
        await f.WaitForStatusAsync(id, "REJECTED");
    }

    // ---- DocuSign webhook + reconciliation ---------------------------------------------------------------------------------

    private async Task<(string ProcessId, string EnvelopeId)> SeedInFlightDocuSign()
    {
        var id = Ids.New("sig");
        var envId = Guid.NewGuid().ToString();
        FakeVendors.Envelopes[envId] = new FakeVendors.DsEnvelope
        {
            Id = envId, Status = "sent", Ref = id, FileName = "c.pdf", Document = TestFixture.DocBytes,
            Signers = [new() { RecipientId = "1", Email = "a@x.com", Name = "A", Status = "sent" }, new() { RecipientId = "2", Email = "b@x.com", Name = "B", Status = "sent" }]
        };
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTime.UtcNow.AddMinutes(-5);
        var p = new SignatureProcess
        {
            Id = id, ExternalId = "SEED-" + id, BusinessStatus = BusinessStatus.SIGNATURE_IN_PROGRESS, SignatureType = "ADVANCED", CorrelationId = "c",
            CreatedAt = now, UpdatedAt = now, RequestJson = "{}"
        };
        p.Signers.Add(new Signer { Id = Ids.New("sgn"), ProcessId = id, ExternalId = "s1", Name = "A", Document = Cpf, Position = 0 });
        p.Signers.Add(new Signer { Id = Ids.New("sgn"), ProcessId = id, ExternalId = "s2", Name = "B", Document = Cpf, Position = 1 });
        db.Processes.Add(p);
        db.ProviderProcesses.Add(new ProviderProcess
        {
            ProcessId = id, ProviderCode = "DOCUSIGN", ExternalReference = id, ProviderProcessId = envId, NormalizedStatus = "PENDING", MetadataJson = "{}", UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (id, envId);
    }

    private Task<HttpResponseMessage> PostDocuSignWebhook(HttpClient client, string body, string? signature) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/docusign")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { { "X-DocuSign-Signature-1", signature ?? "" } }
        });

    private static string DsEvent(string envelopeId, string @event = "envelope-completed") =>
        JsonSerializer.Serialize(new { @event, data = new { accountId = "ACC", envelopeId } });

    [Fact]
    public async Task A_signed_DocuSign_webhook_reconciles_the_process_immediately()
    {
        var (id, envId) = await SeedInFlightDocuSign();
        FakeVendors.DsComplete(envId);
        var body = DsEvent(envId);
        var r = await PostDocuSignWebhook(f.Client, body, FakeVendors.ConnectSignature(body));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().Should().Be("CORRECTED");
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNED");
        (await Detail(id)).GetProperty("signers").EnumerateArray().Should().OnlyContain(s => s.GetProperty("signed").GetBoolean());
        (await EventTypes(id)).Should().Contain(["PROVIDER_WEBHOOK_RECEIVED", "RECONCILIATION_CORRECTED"]);
        var rec = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/reconciliations")).GetProperty("items").EnumerateArray().ToList();
        rec.Should().Contain(x => x.GetProperty("trigger").GetString() == "WEBHOOK" && x.GetProperty("outcome").GetString() == "CORRECTED");
    }

    [Fact]
    public async Task Webhook_with_a_bad_or_missing_signature_is_refused_and_changes_nothing()
    {
        var (id, envId) = await SeedInFlightDocuSign();
        FakeVendors.DsComplete(envId);
        var body = DsEvent(envId);
        (await PostDocuSignWebhook(f.Client, body, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostDocuSignWebhook(f.Client, body, FakeVendors.ConnectSignature(body, "wrong-key"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostDocuSignWebhook(f.Client, body, FakeVendors.ConnectSignature(body + " "))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostDocuSignWebhook(f.Client, body, "not-base64!!")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        (await EventTypes(id)).Should().NotContain("PROVIDER_WEBHOOK_RECEIVED");
    }

    [Fact]
    public async Task The_webhook_payload_is_only_a_trigger_the_provider_is_the_source_of_truth()
    {
        var (id, envId) = await SeedInFlightDocuSign(); // the envelope is still "sent" at the provider
        var body = DsEvent(envId, "envelope-completed"); // ...but the (authentic) webhook claims completion
        var r = await PostDocuSignWebhook(f.Client, body, FakeVendors.ConnectSignature(body));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().Should().Be("CONSISTENT");
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNATURE_IN_PROGRESS");
        (await EventTypes(id)).Should().Contain("PROVIDER_WEBHOOK_RECEIVED");
    }

    [Fact]
    public async Task Webhooks_for_unknown_references_and_unknown_providers_leak_nothing()
    {
        var body = DsEvent("00000000-0000-0000-0000-000000000000");
        var unknown = await PostDocuSignWebhook(f.Client, body, FakeVendors.ConnectSignature(body));
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("ignored");
        (await f.Client.PostAsync("/v1/webhooks/clicksign", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var garbage = "not json";
        (await PostDocuSignWebhook(f.Client, garbage, FakeVendors.ConnectSignature(garbage))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_routes_are_public_even_when_authentication_is_enabled_and_still_need_the_signature()
    {
        using var api = f.CreateAuthClient();
        var (id, envId) = await SeedInFlightDocuSign();
        FakeVendors.DsComplete(envId);
        var body = DsEvent(envId);
        (await PostDocuSignWebhook(api, body, "bad")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostDocuSignWebhook(api, body, FakeVendors.ConnectSignature(body))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Detail(id)).GetProperty("businessStatus").GetString().Should().Be("SIGNED");
        // everything else still demands a token
        (await api.GetAsync($"/v1/signature-processes/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scheduled_or_manual_reconciliation_corrects_a_DocuSign_process_without_any_webhook()
    {
        var (id, envId) = await SeedInFlightDocuSign();
        FakeVendors.DsSign(envId, 0);
        var r = await f.Client.PostAsync($"/v1/signature-processes/{id}/reconcile", null);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().Should().Be("CORRECTED");
        var d = await Detail(id);
        d.GetProperty("businessStatus").GetString().Should().Be("PARTIALLY_SIGNED");
        d.GetProperty("signers")[0].GetProperty("signed").GetBoolean().Should().BeTrue();
        d.GetProperty("signers")[1].GetProperty("signed").GetBoolean().Should().BeFalse();
    }

    // ---- Lacuna ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lacuna_end_to_end_creates_one_document_with_the_flow_and_stores_signed_document_and_evidence()
    {
        var (id, _) = await CreateWithAsync("LACUNA", Signer("Maria Souza", "maria@example.com", 1) + "," + Signer("João Lima", "joao@example.com", 2));

        await WaitAsync(() => Task.FromResult(FakeVendors.LzByRef(id) is not null), 30, "Lacuna document");
        var doc = FakeVendors.LzByRef(id)!;
        doc.Content.Should().Equal(TestFixture.DocBytes);
        doc.Actions.Select(a => (a.Name, a.Email, a.Identifier, a.Step)).Should().Equal(("Maria Souza", "maria@example.com", Cpf, 1), ("João Lima", "joao@example.com", Cpf, 2));
        FakeVendors.Documents.Values.Count(d => d.Description == "orchestrator:" + id).Should().Be(1);

        doc.Actions[0].Status = "Completed";
        await f.WaitForStatusAsync(id, "PARTIALLY_SIGNED");
        FakeVendors.LzComplete(doc.Id);
        await f.WaitForStatusAsync(id, "COMPLETED");
        var arts = (await Artifacts(id)).ToDictionary(a => a.GetProperty("type").GetString()!);
        Encoding.ASCII.GetString(await Download(id, arts["SIGNED_DOCUMENT"])).Should().Contain("%%LACUNA-SIGNED");
        Encoding.ASCII.GetString(await Download(id, arts["EVIDENCE"])).Should().Contain("lacuna signature report");
        (await Detail(id)).GetProperty("provider").GetString().Should().Be("LACUNA");
    }

    [Fact]
    public async Task Lacuna_transient_failure_is_retried_and_cancel_reaches_the_provider()
    {
        FakeVendors.Fail("lz:create", 1, 503);
        var (id, _) = await CreateWithAsync("LACUNA", Signer("Ana", "ana@example.com"));
        await WaitAsync(() => Task.FromResult(FakeVendors.LzByRef(id) is not null), 30, "Lacuna document");
        var doc = FakeVendors.LzByRef(id)!;
        (await EventTypes(id)).Should().Contain("OPERATION_RETRY_SCHEDULED");
        (await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        doc.Status.Should().Be("Canceled");
        doc.CancelReason.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Lacuna_webhook_requires_the_shared_secret_and_reconciles()
    {
        var (id, _) = await CreateWithAsync("LACUNA", Signer("Ana", "ana@example.com"));
        await WaitAsync(() => Task.FromResult(FakeVendors.LzByRef(id) is not null), 30, "Lacuna document");
        var doc = FakeVendors.LzByRef(id)!;
        // park the polling loop's effect: complete at the provider, then notify
        FakeVendors.LzComplete(doc.Id);
        var body = JsonSerializer.Serialize(new { type = "DocumentConcluded", documentId = doc.Id });
        HttpRequestMessage Req(string? secret) => new(HttpMethod.Post, "/v1/webhooks/lacuna")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"), Headers = { { "X-Webhook-Secret", secret ?? "" } }
        };
        (await f.Client.SendAsync(Req(null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await f.Client.SendAsync(Req("wrong"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await f.Client.SendAsync(Req(FakeVendors.LacunaSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
        await f.WaitForStatusAsync(id, "COMPLETED");
        (await EventTypes(id)).Should().Contain("PROVIDER_WEBHOOK_RECEIVED");
    }

    // ---- secrets ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Credentials_and_tokens_never_reach_logs_events_or_responses()
    {
        var (id, _) = await CreateWithAsync("DOCUSIGN", Signer("Ana", "ana@example.com"));
        var env = FakeVendors.DsByRef(id)!;
        FakeVendors.DsComplete(env.Id);
        await f.WaitForStatusAsync(id, "COMPLETED");
        var secrets = new[] { FakeVendors.DsToken, FakeVendors.ConnectKey, FakeVendors.LzApiKey, FakeVendors.LacunaSecret, "BEGIN PRIVATE KEY", FakeVendors.DsAssertions.Last() };
        var surfaces = string.Join("\n", CapturingLoggerProvider.Lines.ToArray()) + (await Detail(id)).ToString()
            + (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=200")).ToString()
            + (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?pageSize=200")).ToString()
            + (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/provider")).ToString();
        foreach (var s in secrets) surfaces.Should().NotContain(s);
    }

    [Fact]
    public async Task Swagger_does_not_expose_the_webhook_routes_or_any_credential()
    {
        var json = await f.Client.GetStringAsync("/swagger/v1/swagger.json");
        json.Should().NotContain("/v1/webhooks").And.NotContain(FakeVendors.ConnectKey);
    }
}
