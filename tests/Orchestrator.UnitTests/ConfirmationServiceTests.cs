using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Confirmation;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Resilience;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers;
using Xunit;

namespace Orchestrator.UnitTests;

public class ConfirmationServiceTests
{
    private sealed class CapturingNotifier : IConfirmationNotifier
    {
        public List<ConfirmationMessage> Sent { get; } = [];
        public string LastCode => Sent[^1].Code;
        public Task SendAsync(ConfirmationMessage m, CancellationToken ct) { Sent.Add(m); return Task.CompletedTask; }
    }

    private sealed class Env
    {
        public OrchestratorDbContext Db = null!;
        public FixedClock Clock = null!;
        public CapturingNotifier Notifier = new();
        public ConfirmationService Svc = null!;
        public SignerAdvancer Advancer = null!;
        public FakeProviderAdapter Provider = null!;
        public static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        public static Env Create(ConfirmationOptions? o = null)
        {
            var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var clock = new FixedClock(T0);
            var notifier = new CapturingNotifier();
            var recorder = new EventRecorder(db, clock);
            var retry = new RetryPolicy(Options.Create(new RetryOptions()));
            var svc = new ConfirmationService(db, notifier, recorder, clock, Options.Create(o ?? new ConfirmationOptions()), retry, NullLogger<ConfirmationService>.Instance);
            var provider = new FakeProviderAdapter(db, clock, Options.Create(new FakeProviderOptions { SignDelaySeconds = 10 }));
            return new Env { Db = db, Clock = clock, Notifier = notifier, Svc = svc, Provider = provider, Advancer = new SignerAdvancer(db, provider, recorder, clock, svc) };
        }

        /// <summary>Process with one signer and one EMAIL confirmation, code already sent.</summary>
        public async Task<(SignatureProcess P, Signer S, SignerConfirmation C)> SentAsync()
        {
            var p = new SignatureProcess
            {
                Id = "sig_1", ExternalId = "E", BusinessStatus = BusinessStatus.SIGNATURE_IN_PROGRESS, SignatureType = "ADVANCED",
                CorrelationId = "c", CreatedAt = T0, UpdatedAt = T0, RequestJson = "{}"
            };
            var s = new Signer { Id = "sgn_1", ProcessId = p.Id, ExternalId = "s", Name = "Ana", Document = "12345678909", Email = "ana@x.com", ConfirmationChannels = "EMAIL" };
            p.Signers.Add(s);
            var c = new SignerConfirmation { Id = "cnf_1", ProcessId = p.Id, SignerId = s.Id, Channel = "EMAIL", Status = ConfirmationStatus.SENDING, CreatedAt = T0 };
            Db.Processes.Add(p);
            Db.Confirmations.Add(c);
            var op = Svc.NewSendOperation(p.Id, c, 0, T0);
            Db.Operations.Add(op);
            await Db.SaveChangesAsync();
            await Svc.SendAsync(op, "corr", null, default);
            await Db.SaveChangesAsync();
            return (p, s, c);
        }

        public Task<ConfirmationResult> Verify(string code) => Svc.VerifyAsync("sig_1", "sgn_1", "EMAIL", code, "corr", default);
        public Task<ResendResult> Resend() => Svc.ResendAsync("sig_1", "sgn_1", "EMAIL", "corr", default);
        public Task<SignerConfirmation> Conf() => Db.Confirmations.AsNoTracking().SingleAsync();
    }

    private static string Wrong(string code) => code == "000000" ? "111111" : "000000";

    [Fact]
    public async Task Send_delivers_a_code_and_stores_only_its_hash_and_expiry()
    {
        var e = Env.Create();
        await e.SentAsync();
        var code = e.Notifier.LastCode;
        code.Should().MatchRegex("^[0-9]{6}$");
        var c = await e.Conf();
        (c.Status, c.SendCount, c.ExpiresAt).Should().Be((ConfirmationStatus.SENT, 1, Env.T0.AddMinutes(10)));
        c.CodeHash.Should().NotBeNull().And.NotContain(code).And.HaveLength(64);
        e.Notifier.Sent.Single().Destination.Should().Be("ana@x.com");
    }

    [Fact]
    public async Task The_code_never_appears_in_journal_operations_or_outbox()
    {
        var e = Env.Create();
        await e.SentAsync();
        var code = e.Notifier.LastCode;
        await Assert.ThrowsAsync<CodeRejectedException>(() => e.Verify(Wrong(code)));
        await e.Verify(code);
        var everything = string.Join("|", (await e.Db.Journal.Select(j => j.MetadataJson).ToListAsync())
            .Concat(await e.Db.Operations.Select(o => (o.InputJson ?? "") + (o.OutputJson ?? "") + (o.ErrorJson ?? "")).ToListAsync())
            .Concat(await e.Db.Outbox.Select(o => o.PayloadJson).ToListAsync()));
        everything.Should().NotContain(code);
        (await e.Db.Journal.Select(j => j.Type).ToListAsync()).Should().Contain(["CONFIRMATION_SENT", "CONFIRMATION_CODE_REJECTED", "CONFIRMATION_CONFIRMED"]);
    }

    [Fact]
    public async Task Correct_code_confirms_once_and_is_recorded_as_a_verify_operation()
    {
        var e = Env.Create();
        await e.SentAsync();
        (await e.Verify(e.Notifier.LastCode)).Status.Should().Be("CONFIRMED");
        var c = await e.Conf();
        (c.Status, c.CodeHash, c.ConfirmedAt).Should().Be((ConfirmationStatus.CONFIRMED, null, Env.T0));
        (await e.Verify("123456")).Status.Should().Be("CONFIRMED"); // idempotent once confirmed
        var ops = await e.Db.Operations.Where(o => o.Type == OperationType.CONFIRMATION_VERIFY).ToListAsync();
        ops.Should().ContainSingle().Which.OutputJson.Should().Contain("CONFIRMED");
    }

    [Fact]
    public async Task Wrong_codes_count_down_and_the_last_one_locks()
    {
        var e = Env.Create();
        await e.SentAsync();
        var wrong = Wrong(e.Notifier.LastCode);
        for (var left = 4; left >= 1; left--)
            (await Assert.ThrowsAsync<CodeRejectedException>(() => e.Verify(wrong))).AttemptsRemaining.Should().Be(left);
        await Assert.ThrowsAsync<CodeLockedException>(() => e.Verify(wrong));
        (await e.Conf()).Status.Should().Be(ConfirmationStatus.LOCKED);
        // even the right code is refused once locked
        await Assert.ThrowsAsync<CodeLockedException>(() => e.Verify(e.Notifier.LastCode));
        (await e.Db.Journal.Select(j => j.Type).ToListAsync()).Should().Contain("CONFIRMATION_LOCKED");
    }

    [Fact]
    public async Task Expired_code_is_refused_and_a_resend_issues_a_fresh_one()
    {
        var e = Env.Create();
        await e.SentAsync();
        var old = e.Notifier.LastCode;
        e.Clock.UtcNow = Env.T0.AddMinutes(10).AddSeconds(1);
        await Assert.ThrowsAsync<CodeExpiredException>(() => e.Verify(old));
        (await e.Db.Journal.Select(j => j.Type).ToListAsync()).Should().Contain("CONFIRMATION_CODE_EXPIRED");

        await e.Resend();
        var op = await e.Db.Operations.Where(o => o.Type == OperationType.CONFIRMATION_SEND && o.Sequence == 1).SingleAsync();
        await e.Svc.SendAsync(op, "corr", null, default);
        await e.Db.SaveChangesAsync();
        (await e.Verify(e.Notifier.LastCode)).Status.Should().Be("CONFIRMED");
    }

    [Fact]
    public async Task Resend_has_a_minimum_interval_and_a_ceiling_of_sends()
    {
        var e = Env.Create(new ConfirmationOptions { MaxSends = 2, ResendMinIntervalSeconds = 30 });
        await e.SentAsync();
        e.Clock.UtcNow = Env.T0.AddSeconds(10);
        var tooSoon = await Assert.ThrowsAsync<RateLimitedException>(() => e.Resend());
        tooSoon.RetryAfterSeconds.Should().Be(20);

        e.Clock.UtcNow = Env.T0.AddSeconds(31);
        (await e.Resend()).Status.Should().Be("SENDING");
        (await e.Db.Outbox.CountAsync(o => o.Queue == "notification")).Should().Be(1); // the resend (the first send was seeded directly)
        await Assert.ThrowsAsync<ConflictException>(() => e.Resend()); // already being sent

        var op = await e.Db.Operations.SingleAsync(o => o.Type == OperationType.CONFIRMATION_SEND && o.Sequence == 1);
        await e.Svc.SendAsync(op, "corr", null, default);
        await e.Db.SaveChangesAsync();
        e.Clock.UtcNow = Env.T0.AddMinutes(5);
        var limit = await Assert.ThrowsAsync<RateLimitedException>(() => e.Resend());
        limit.RetryAfterSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Resending_resets_the_attempts_of_a_locked_code()
    {
        var e = Env.Create();
        await e.SentAsync();
        var wrong = Wrong(e.Notifier.LastCode);
        for (var i = 0; i < 5; i++) await Assert.ThrowsAnyAsync<Exception>(() => e.Verify(wrong));
        e.Clock.UtcNow = Env.T0.AddSeconds(40);
        await e.Resend();
        var op = await e.Db.Operations.SingleAsync(o => o.Type == OperationType.CONFIRMATION_SEND && o.Sequence == 1);
        await e.Svc.SendAsync(op, "corr", null, default);
        await e.Db.SaveChangesAsync();
        var c = await e.Conf();
        (c.Status, c.FailedAttempts).Should().Be((ConfirmationStatus.SENT, 0));
    }

    [Fact]
    public async Task Verify_requires_a_delivered_code_and_a_non_terminal_process()
    {
        var e = Env.Create();
        var (p, _, c) = await e.SentAsync();
        c = await e.Db.Confirmations.SingleAsync();
        c.Status = ConfirmationStatus.SENDING;
        await e.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => e.Verify("123456"));
        c.Status = ConfirmationStatus.SENT;
        (await e.Db.Processes.SingleAsync()).TransitionBusiness(BusinessStatus.CANCELLED, Env.T0);
        await e.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => e.Verify("123456"));
        await Assert.ThrowsAsync<ValidationException>(() => e.Verify("abc"));
        await Assert.ThrowsAsync<NotFoundException>(() => e.Svc.VerifyAsync("sig_1", "sgn_1", "PIGEON", "123456", "c", default));
    }

    [Fact]
    public async Task Advancer_sends_codes_only_on_the_signers_turn_and_releases_after_confirmation()
    {
        var e = Env.Create();
        var p = new SignatureProcess { Id = "sig_9", ExternalId = "E9", BusinessStatus = BusinessStatus.SIGNATURE_IN_PROGRESS, SignatureType = "ADVANCED", CorrelationId = "c", CreatedAt = Env.T0, UpdatedAt = Env.T0, RequestJson = "{}" };
        var a = new Signer { Id = "sgn_a", ProcessId = p.Id, ExternalId = "a", Name = "A", Document = "12345678909", Position = 0, Order = 1, Email = "a@x.com", ConfirmationChannels = "EMAIL" };
        var b = new Signer { Id = "sgn_b", ProcessId = p.Id, ExternalId = "b", Name = "B", Document = "12345678909", Position = 1, Order = 2, Email = "b@x.com", ConfirmationChannels = "EMAIL" };
        p.Signers.AddRange([a, b]);
        e.Db.Processes.Add(p);
        e.Db.Confirmations.AddRange(
            new SignerConfirmation { Id = "cnf_a", ProcessId = p.Id, SignerId = a.Id, Channel = "EMAIL", CreatedAt = Env.T0 },
            new SignerConfirmation { Id = "cnf_b", ProcessId = p.Id, SignerId = b.Id, Channel = "EMAIL", CreatedAt = Env.T0 });
        await e.Provider.CreateProcessAsync(new CreateProviderProcessRequest(p.Id, "E9", "ADVANCED", [new(0, "A"), new(1, "B")], 1, Gated: true), default);
        await e.Db.SaveChangesAsync();
        await e.Provider.SendDocumentAsync(p.Id, "c.pdf", default);

        await e.Advancer.AdvanceAsync(p, "corr", null, default);
        await e.Db.SaveChangesAsync();
        var sends = await e.Db.Operations.Where(o => o.Type == OperationType.CONFIRMATION_SEND).ToListAsync();
        sends.Should().ContainSingle().Which.InputJson.Should().Contain("cnf_a"); // B waits for A
        a.ReleasedAt.Should().BeNull();

        var c = await e.Db.Confirmations.SingleAsync(x => x.Id == "cnf_a");
        c.Status = ConfirmationStatus.CONFIRMED;
        await e.Advancer.AdvanceAsync(p, "corr", null, default);
        await e.Db.SaveChangesAsync();
        a.ReleasedAt.Should().Be(Env.T0);
        b.ReleasedAt.Should().BeNull();
        (await e.Db.Operations.CountAsync(o => o.Type == OperationType.CONFIRMATION_SEND)).Should().Be(1);

        // the provider signs A only after the delay; B only becomes eligible once A signed
        e.Clock.UtcNow = Env.T0.AddSeconds(11);
        (await e.Provider.GetStatusAsync(p.Id, default)).SignedPositions.Should().Equal(0);
        a.Signed = true;
        await e.Advancer.AdvanceAsync(p, "corr", null, default);
        await e.Db.SaveChangesAsync();
        (await e.Db.Operations.CountAsync(o => o.Type == OperationType.CONFIRMATION_SEND)).Should().Be(2);
        (await e.Db.Journal.Select(j => j.Type).ToListAsync()).Should().Contain(["CONFIRMATION_REQUESTED", "SIGNER_RELEASED"]);
    }

    [Fact]
    public async Task Gated_provider_never_signs_a_signer_that_was_not_released()
    {
        var e = Env.Create();
        await e.Provider.CreateProcessAsync(new CreateProviderProcessRequest("ref1", "E", "ADVANCED", [new(0, "A"), new(1, "B")], 1, Gated: true), default);
        await e.Db.SaveChangesAsync();
        await e.Provider.SendDocumentAsync("ref1", "c.pdf", default);
        e.Clock.UtcNow = Env.T0.AddHours(1);
        var st = await e.Provider.GetStatusAsync("ref1", default);
        st.NormalizedStatus.Should().Be(ProviderStatuses.Pending);
        st.SignedPositions.Should().BeEmpty();

        await e.Provider.ReleaseSignerAsync("ref1", 1, default);
        await e.Provider.ReleaseSignerAsync("ref1", 1, default); // idempotent
        e.Clock.UtcNow = Env.T0.AddHours(1).AddSeconds(5);
        (await e.Provider.GetStatusAsync("ref1", default)).SignedPositions.Should().BeEmpty(); // delay not elapsed
        e.Clock.UtcNow = Env.T0.AddHours(1).AddSeconds(11);
        var after = await e.Provider.GetStatusAsync("ref1", default);
        after.NormalizedStatus.Should().Be(ProviderStatuses.PartiallySigned);
        after.SignedPositions.Should().Equal(1);
        await e.Provider.ReleaseSignerAsync("ref1", 0, default);
        e.Clock.UtcNow = Env.T0.AddHours(2);
        (await e.Provider.GetStatusAsync("ref1", default)).NormalizedStatus.Should().Be(ProviderStatuses.Signed);
    }
}
