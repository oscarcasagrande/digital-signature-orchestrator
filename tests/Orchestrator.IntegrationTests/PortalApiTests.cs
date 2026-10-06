using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Orchestrator.Domain.StateMachines;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class OperatorApi
{
    public static async Task<HttpResponseMessage> AsAsync(this TestFixture f, HttpMethod method, string url, string? operatorId, object? body = null,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (operatorId is not null) req.Headers.Add("X-Operator-Id", operatorId);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) req.Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await f.Client.SendAsync(req);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<JsonElement> LastEventAsync(this TestFixture f, string id, string type) =>
        (await f.EventsAsync(id)).Last(e => e.GetProperty("type").GetString() == type);

    public static async Task<JsonElement> ListAsync(this TestFixture f, string query) =>
        await f.Client.GetFromJsonAsync<JsonElement>("/v1/signature-processes" + query);

    public static List<string> Ids(this JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("processId").GetString()!).ToList();
}

[Collection("integration")]
public class PortalApiTests(TestFixture f)
{
    [Fact]
    public async Task List_returns_the_columns_of_the_portal()
    {
        var id = await f.CreateProcessAsync("PORTAL-COLS-" + Guid.NewGuid().ToString("N")[..6], signers: 2);
        await f.WaitForStatusAsync(id, "COMPLETED");
        var list = await f.ListAsync("?q=" + id);
        var item = list.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("processId").GetString().Should().Be(id);
        item.GetProperty("documentFileName").GetString().Should().Be("c.pdf");
        item.GetProperty("provider").GetString().Should().Be("SIMULATED");
        item.GetProperty("signersSigned").GetInt32().Should().Be(2);
        item.GetProperty("signersTotal").GetInt32().Should().Be(2);
        item.GetProperty("businessStatus").GetString().Should().Be("COMPLETED");
        item.GetProperty("operationalStatus").GetString().Should().Be("READY");
        item.GetProperty("sla").GetString().Should().Be("OK");
        item.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);
        list.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task List_is_filterable_by_business_and_operational_status_case_insensitively()
    {
        var done = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(done, "COMPLETED");
        var cancelled = await f.CreateProcessAsync();
        await f.Client.PostAsync($"/v1/signature-processes/{cancelled}/cancel", null);
        var down = await f.CreateProcessAsync("SIM-DOWN-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForOperationalAsync(down, "DLQ");

        var completed = (await f.ListAsync("?status=COMPLETED&pageSize=200")).Ids();
        completed.Should().Contain(done).And.NotContain(cancelled).And.NotContain(down);
        (await f.ListAsync("?status=cancelled&pageSize=200")).Ids().Should().Contain(cancelled).And.NotContain(done);
        var dlq = await f.ListAsync("?operationalStatus=DLQ&pageSize=200");
        dlq.Ids().Should().Contain(down).And.NotContain(done);
        dlq.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("processId").GetString() == down).GetProperty("sla").GetString().Should().Be("ALERT");
        (await f.ListAsync($"?status=COMPLETED&operationalStatus=DLQ&q={down}")).Ids().Should().BeEmpty(); // filters combine (AND)
        (await f.ListAsync($"?status=VALIDATING&operationalStatus=DLQ&q={down}")).Ids().Should().Equal(down);
    }

    [Fact]
    public async Task Search_matches_part_of_the_id_or_the_external_id_without_case_sensitivity()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var id = await f.CreateProcessAsync("Search-" + token + "-X");
        (await f.ListAsync("?q=" + token.ToUpperInvariant())).Ids().Should().Equal(id);
        (await f.ListAsync("?q=search-" + token)).Ids().Should().Equal(id);
        (await f.ListAsync("?q=" + id[^10..])).Ids().Should().Contain(id);
        (await f.ListAsync("?q=nothing-matches-" + Guid.NewGuid().ToString("N"))).Ids().Should().BeEmpty();
        (await f.ListAsync("?q=100%25_literal")).Ids().Should().BeEmpty(); // wildcard characters are literal
    }

    [Fact]
    public async Task List_is_paginated_newest_first_without_repeating_items()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add(await f.CreateProcessAsync($"Page-{token}-{i}"));
        var p1 = await f.ListAsync($"?q=Page-{token}&pageSize=2&page=1");
        var p2 = await f.ListAsync($"?q=Page-{token}&pageSize=2&page=2");
        var p3 = await f.ListAsync($"?q=Page-{token}&pageSize=2&page=3");
        p1.GetProperty("total").GetInt32().Should().Be(5);
        var all = p1.Ids().Concat(p2.Ids()).Concat(p3.Ids()).ToList();
        all.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        all.Should().Equal(Enumerable.Reverse(ids).ToList()); // newest first
        p3.Ids().Should().HaveCount(1);
        (await f.ListAsync("?pageSize=100000")).GetProperty("pageSize").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task Unknown_status_filters_are_rejected()
    {
        var r = await f.Client.GetAsync("/v1/signature-processes?status=NOPE&operationalStatus=ALSO_NOPE");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await r.Content.ReadAsStringAsync();
        text.Should().Contain("status").And.Contain("operationalStatus");
    }

    [Fact]
    public async Task Sla_is_alert_for_failed_states_and_for_processes_waiting_too_long_and_ok_otherwise()
    {
        var failed = await f.SeedAsync(status: BusinessStatus.FAILED, updatedSecondsAgo: 5);
        var rejected = await f.SeedAsync(status: BusinessStatus.REJECTED, updatedSecondsAgo: 5);
        var old = await f.SeedAsync(sentSecondsAgo: -3600, updatedSecondsAgo: 4000); // created > 60 min ago, still in progress
        var recent = await f.SeedAsync(sentSecondsAgo: -3600, updatedSecondsAgo: 5);
        var oldCompleted = await f.SeedAsync(status: BusinessStatus.COMPLETED, updatedSecondsAgo: 4000);

        async Task<string> Sla(string id) => (await f.ListAsync("?q=" + id)).GetProperty("items")[0].GetProperty("sla").GetString()!;
        (await Sla(failed)).Should().Be("ALERT");
        (await Sla(rejected)).Should().Be("ALERT");
        (await Sla(old)).Should().Be("ALERT");
        (await Sla(recent)).Should().Be("OK");
        (await Sla(oldCompleted)).Should().Be("OK"); // terminal and healthy
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{old}")).GetProperty("sla").GetString().Should().Be("ALERT");
    }

    [Fact]
    public async Task Detail_includes_document_provider_and_sla()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        var d = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");
        d.GetProperty("documentFileName").GetString().Should().Be("c.pdf");
        d.GetProperty("provider").GetString().Should().Be("SIMULATED");
        d.GetProperty("sla").GetString().Should().Be("OK");

        var noProvider = await f.SeedAsync(providerRegistration: false);
        var d2 = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{noProvider}");
        d2.GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);
        d2.GetProperty("documentFileName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Provider_metadata_are_returned_and_the_access_is_audited_with_the_operator()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        var r = await f.AsAsync(HttpMethod.Get, $"/v1/signature-processes/{id}/provider", "maria.ops");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var p = await r.JsonAsync();
        p.GetProperty("provider").GetString().Should().Be("SIMULATED");
        p.GetProperty("providerProcessId").GetString().Should().StartWith("sim_");
        p.GetProperty("externalReference").GetString().Should().Be(id);
        p.GetProperty("normalizedStatus").GetString().Should().Be("SIGNED");
        p.GetProperty("metadata").ValueKind.Should().Be(JsonValueKind.Object);

        var ev = await f.LastEventAsync(id, "PROVIDER_METADATA_INSPECTED");
        ev.GetProperty("actor").GetProperty("type").GetString().Should().Be("OPERATOR");
        ev.GetProperty("actor").GetProperty("id").GetString().Should().Be("maria.ops");
    }

    [Fact]
    public async Task Provider_metadata_not_found_cases()
    {
        (await f.Client.GetAsync("/v1/signature-processes/sig_nope/provider")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var noProvider = await f.SeedAsync(providerRegistration: false);
        (await f.Client.GetAsync($"/v1/signature-processes/{noProvider}/provider")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dead_letters_can_be_filtered_by_process()
    {
        var a = await f.CreateProcessAsync("SIM-DOWN-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await f.CreateProcessAsync("SIM-DOWN-" + Guid.NewGuid().ToString("N")[..6]);
        await f.WaitForOperationalAsync(a, "DLQ");
        await f.WaitForOperationalAsync(b, "DLQ");
        var list = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/dead-letters?processId={a}");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty().And.OnlyContain(i => i.GetProperty("processId").GetString() == a);
        (await f.Client.GetFromJsonAsync<JsonElement>("/v1/dead-letters?processId=sig_nope")).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Manual_reconciliation_is_always_audited_even_when_consistent()
    {
        var id = await f.SeedAsync(sentSecondsAgo: -3600);
        (await f.AsAsync(HttpMethod.Post, $"/v1/signature-processes/{id}/reconcile", "ops.joao")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ev = await f.LastEventAsync(id, "RECONCILIATION_REQUESTED");
        ev.GetProperty("actor").GetProperty("type").GetString().Should().Be("OPERATOR");
        ev.GetProperty("actor").GetProperty("id").GetString().Should().Be("ops.joao");
        ev.GetProperty("metadata").GetProperty("trigger").GetString().Should().Be("MANUAL");

        var anonymous = await f.SeedAsync(sentSecondsAgo: -3600);
        await f.Client.PostAsync($"/v1/signature-processes/{anonymous}/reconcile", null);
        (await f.LastEventAsync(anonymous, "RECONCILIATION_REQUESTED")).GetProperty("actor").GetProperty("id").GetString().Should().Be("api");
    }
}

[Collection("integration")]
public class ActorTests(TestFixture f)
{
    private static string Actor(JsonElement ev) => ev.GetProperty("actor").GetProperty("type").GetString() + "/" + ev.GetProperty("actor").GetProperty("id").GetString();

    [Fact]
    public async Task Process_creation_cancel_and_download_link_record_the_operator()
    {
        var key = Guid.NewGuid().ToString();
        var created = await f.AsAsync(HttpMethod.Post, "/v1/signature-processes", "ana.ops", TestFixture.Payload("ACTOR-1"), key);
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await created.JsonAsync()).GetProperty("processId").GetString()!;
        Actor(await f.LastEventAsync(id, "PROCESS_CREATED")).Should().Be("OPERATOR/ana.ops");

        (await f.AsAsync(HttpMethod.Post, $"/v1/signature-processes/{id}/cancel", "bruno@corp")).StatusCode.Should().Be(HttpStatusCode.OK);
        Actor(await f.LastEventAsync(id, "PROCESS_CANCELLED")).Should().Be("OPERATOR/bruno@corp");

        var done = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(done, "COMPLETED");
        (await f.AsAsync(HttpMethod.Post, $"/v1/signature-processes/{done}/download-link", "carla:ops", new { type = "SIGNED_DOCUMENT" })).StatusCode.Should().Be(HttpStatusCode.OK);
        Actor(await f.LastEventAsync(done, "DOWNLOAD_LINK_ISSUED")).Should().Be("OPERATOR/carla:ops");
    }

    [Fact]
    public async Task Reprocess_records_the_operator()
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        TestFixture.ToggleDown[key] = 403;
        try
        {
            var id = await f.CreateProcessAsync(documentUrl: f.ToggleUrl(key));
            await f.WaitForOperationalAsync(id, "MANUAL_ACTION");
            TestFixture.ToggleDown.TryRemove(key, out _);
            (await f.AsAsync(HttpMethod.Post, $"/v1/signature-processes/{id}/retry", "dora-ops", new { reason = "fixed" })).StatusCode.Should().Be(HttpStatusCode.OK);
            Actor(await f.LastEventAsync(id, "OPERATION_REPROCESS_REQUESTED")).Should().Be("OPERATOR/dora-ops");
        }
        finally { TestFixture.ToggleDown.TryRemove(key, out _); }
    }

    [Fact]
    public async Task Proofing_actions_record_the_operator()
    {
        var key = Guid.NewGuid().ToString();
        var r = await f.AsAsync(HttpMethod.Post, "/v1/proofing-sessions", "edu.ops", ProofingApi.Body("ACTOR-P", "[\"PERSON_DATA\"]"), key);
        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await r.JsonAsync()).GetProperty("sessionId").GetString()!;
        Actor((await f.SessionEventsAsync(id)).First(e => e.GetProperty("type").GetString() == "PROOFING_SESSION_CREATED")).Should().Be("OPERATOR/edu.ops");
    }

    [Fact]
    public async Task Without_the_header_the_actor_is_the_api_consumer()
    {
        var id = await f.CreateProcessAsync();
        Actor(await f.LastEventAsync(id, "PROCESS_CREATED")).Should().Be("CONSUMER/api");
        await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null);
        Actor(await f.LastEventAsync(id, "PROCESS_CANCELLED")).Should().Be("CONSUMER/api");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("<script>")]
    public async Task Invalid_operator_ids_are_rejected(string op)
    {
        var r = await f.AsAsync(HttpMethod.Get, "/v1/signature-processes", op);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("X-Operator-Id");
    }

    [Fact]
    public async Task Operator_id_longer_than_100_characters_is_rejected_and_100_is_accepted()
    {
        (await f.AsAsync(HttpMethod.Get, "/v1/signature-processes", new string('a', 101))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.AsAsync(HttpMethod.Get, "/v1/signature-processes", new string('a', 100))).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
