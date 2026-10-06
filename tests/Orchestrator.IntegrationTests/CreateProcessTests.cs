using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

[Collection("integration")]
public class CreateProcessTests(TestFixture f)
{
    [Fact]
    public async Task First_creation_returns_202_and_replay_returns_200_with_same_process()
    {
        var key = Guid.NewGuid().ToString();
        var body = TestFixture.Payload("IDEMP-1");
        var r1 = await f.CreateAsync(key, body);
        r1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        r1.Headers.Location.Should().NotBeNull();
        var p1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        p1.GetProperty("businessStatus").GetString().Should().Be("CREATED");

        var r2 = await f.CreateAsync(key, body);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        var p2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        p2.GetProperty("processId").GetString().Should().Be(p1.GetProperty("processId").GetString());

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Processes.CountAsync(p => p.ExternalId == "IDEMP-1")).Should().Be(1);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_a_conflict()
    {
        var key = Guid.NewGuid().ToString();
        (await f.CreateAsync(key, TestFixture.Payload("A"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var r = await f.CreateAsync(key, TestFixture.Payload("B"));
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        var r = await f.Client.PostAsync("/v1/signature-processes",
            new StringContent(TestFixture.Payload(), System.Text.Encoding.UTF8, "application/json"));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task Invalid_payload_is_rejected_and_nothing_persisted()
    {
        var r = await f.CreateAsync(Guid.NewGuid().ToString(), """{"externalId":"BAD-1","signers":[],"provider":"X"}""");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await r.Content.ReadAsStringAsync();
        text.Should().Contain("signers").And.Contain("provider").And.Contain("correlationId");
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Processes.CountAsync(p => p.ExternalId == "BAD-1")).Should().Be(0);
    }

    [Fact]
    public async Task Fifty_concurrent_requests_with_the_same_key_create_exactly_one_process()
    {
        var key = Guid.NewGuid().ToString();
        var body = TestFixture.Payload("CONCURRENT-1");
        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => f.CreateAsync(key, body)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(49);
        var ids = new HashSet<string>();
        foreach (var r in responses) ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString()!);
        ids.Should().HaveCount(1);

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Processes.CountAsync(p => p.ExternalId == "CONCURRENT-1")).Should().Be(1);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_journaled()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/signature-processes")
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() }, { "X-Correlation-Id", "corr-test-1" } },
            Content = new StringContent(TestFixture.Payload("CORR-1"), System.Text.Encoding.UTF8, "application/json")
        };
        var r = await f.Client.SendAsync(req);
        r.Headers.GetValues("X-Correlation-Id").Should().ContainSingle("corr-test-1");
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processId").GetString();
        var events = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events");
        events.GetProperty("items")[0].GetProperty("correlationId").GetString().Should().Be("corr-test-1");
        events.GetProperty("items")[0].GetProperty("type").GetString().Should().Be("PROCESS_CREATED");
    }
}
