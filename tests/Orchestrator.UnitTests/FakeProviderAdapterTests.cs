using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Providers;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers;
using Xunit;

namespace Orchestrator.UnitTests;

public class FakeProviderAdapterTests
{
    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (FakeProviderAdapter adapter, OrchestratorDbContext db, TestClock clock) Create(string mode = "Auto")
    {
        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new TestClock();
        var opts = Options.Create(new FakeProviderOptions { Mode = mode, SignDelaySeconds = 10 });
        return (new FakeProviderAdapter(db, clock, opts), db, clock);
    }

    private static CreateProviderProcessRequest Req(string reference = "sig_1", string externalId = "X-1", int signers = 2) =>
        new(reference, externalId, "ADVANCED", Enumerable.Range(0, signers).Select(i => new ProviderSigner(i, $"S{i}")).ToList());

    [Fact]
    public async Task Create_is_idempotent_per_external_reference()
    {
        var (a, db, _) = Create();
        var first = await a.CreateProcessAsync(Req(), default);
        await db.SaveChangesAsync();
        var second = await a.CreateProcessAsync(Req(), default);
        second.ProviderProcessId.Should().Be(first.ProviderProcessId);
        (await db.ProviderProcesses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Signs_progressively_then_completes()
    {
        var (a, db, clock) = Create();
        await a.CreateProcessAsync(Req(), default);
        await db.SaveChangesAsync();
        await a.SendDocumentAsync("sig_1", "c.pdf", default);
        await db.SaveChangesAsync();

        (await a.GetStatusAsync("sig_1", default)).NormalizedStatus.Should().Be(ProviderStatuses.Pending);
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var partial = await a.GetStatusAsync("sig_1", default);
        partial.NormalizedStatus.Should().Be(ProviderStatuses.PartiallySigned);
        partial.SignedPositions.Should().Equal(0);
        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var done = await a.GetStatusAsync("sig_1", default);
        done.NormalizedStatus.Should().Be(ProviderStatuses.Signed);
        done.SignedPositions.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Reject_mode_by_external_id_prefix()
    {
        var (a, db, clock) = Create();
        await a.CreateProcessAsync(Req(externalId: "SIM-REJECT-1"), default);
        await db.SaveChangesAsync();
        await a.SendDocumentAsync("sig_1", "c.pdf", default);
        await db.SaveChangesAsync();
        clock.UtcNow = clock.UtcNow.AddSeconds(11);
        (await a.GetStatusAsync("sig_1", default)).NormalizedStatus.Should().Be(ProviderStatuses.Rejected);
    }

    [Fact]
    public async Task Fail_mode_throws_provider_exception()
    {
        var (a, _, _) = Create();
        var act = () => a.CreateProcessAsync(Req(externalId: "SIM-FAIL-1"), default);
        await act.Should().ThrowAsync<ProviderException>();
    }

    [Fact]
    public async Task Cancel_makes_status_cancelled()
    {
        var (a, db, _) = Create();
        await a.CreateProcessAsync(Req(), default);
        await db.SaveChangesAsync();
        await db.SaveChangesAsync();
        await a.CancelAsync("sig_1", default);
        (await a.GetStatusAsync("sig_1", default)).NormalizedStatus.Should().Be(ProviderStatuses.Cancelled);
    }

    [Fact]
    public void Adapter_contract_exposes_no_vendor_types() =>
        typeof(IProviderAdapter).GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType.Namespace))
            .Should().OnlyContain(ns => ns == null || ns.StartsWith("System") || ns.StartsWith("Orchestrator.Application"));
}
