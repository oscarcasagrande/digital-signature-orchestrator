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
public class QueryTests(TestFixture f)
{
    [Fact]
    public async Task Get_process_returns_masked_signers_and_states()
    {
        var id = await f.CreateProcessAsync("QRY-1");
        var p = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}");
        p.GetProperty("processId").GetString().Should().Be(id);
        p.GetProperty("externalId").GetString().Should().Be("QRY-1");
        p.GetProperty("signers")[0].GetProperty("document").GetString().Should().Be("*********09");
        p.GetProperty("businessStatus").GetString().Should().NotBeNullOrEmpty();
        p.GetProperty("operationalStatus").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Operations_and_events_are_paginated()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        var page1 = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?page=1&pageSize=3");
        page1.GetProperty("items").GetArrayLength().Should().Be(3);
        page1.GetProperty("total").GetInt32().Should().BeGreaterThan(3);
        var page2 = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations?page=2&pageSize=3");
        page2.GetProperty("items")[0].GetProperty("operationId").GetString()
            .Should().NotBe(page1.GetProperty("items")[0].GetProperty("operationId").GetString());

        var ev = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events?pageSize=2");
        ev.GetProperty("items").GetArrayLength().Should().Be(2);
        var first = ev.GetProperty("items")[0];
        first.GetProperty("actor").GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/status")]
    [InlineData("/operations")]
    [InlineData("/events")]
    public async Task Unknown_process_is_404(string suffix)
    {
        var r = await f.Client.GetAsync($"/v1/signature-processes/sig_nope{suffix}");
        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Health_endpoint_is_ok() =>
        (await f.Client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
}

[Collection("integration")]
public class CancelTests(TestFixture f)
{
    [Fact]
    public async Task Cancel_before_completion_stops_the_flow_and_is_idempotent()
    {
        var id = await f.CreateProcessAsync();
        var r = await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("businessStatus").GetString().Should().Be("CANCELLED");

        await Task.Delay(4000); // would be enough to complete if the flow were not stopped
        var st = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");
        st.GetProperty("businessStatus").GetString().Should().Be("CANCELLED");

        var again = await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null);
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events")).GetProperty("items");
        events.EnumerateArray().Count(e => e.GetProperty("type").GetString() == "PROCESS_CANCELLED").Should().Be(1);

        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Operations.AnyAsync(o => o.ProcessId == id && o.Status == Orchestrator.Domain.StateMachines.OperationStatus.NOT_STARTED))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_completed_process_is_a_conflict()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        var r = await f.Client.PostAsync($"/v1/signature-processes/{id}/cancel", null);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_unknown_process_is_404() =>
        (await f.Client.PostAsync("/v1/signature-processes/sig_nope/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
}

[Collection("integration")]
public class JournalTests(TestFixture f)
{
    [Fact]
    public async Task Journal_is_append_only()
    {
        await f.CreateProcessAsync();
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var upd = async () => await db.Database.ExecuteSqlRawAsync("UPDATE journal_event SET type = 'TAMPERED'");
        await upd.Should().ThrowAsync<Exception>().WithMessage("*append-only*");
        var del = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM journal_event");
        await del.Should().ThrowAsync<Exception>().WithMessage("*append-only*");
        var trunc = async () => await db.Database.ExecuteSqlRawAsync("TRUNCATE journal_event");
        await trunc.Should().ThrowAsync<Exception>().WithMessage("*append-only*");
    }
}
