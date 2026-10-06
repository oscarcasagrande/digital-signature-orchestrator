using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Artifacts;
using Orchestrator.Application.Providers;
using Orchestrator.Domain.Entities;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Providers;
using Xunit;

namespace Orchestrator.UnitTests;

internal sealed class MemoryStore : IArtifactStore
{
    public Dictionary<string, (byte[] Data, string Type)> Objects { get; } = [];
    public bool Fail { get; set; }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (Fail) throw new IOException("store down");
        Objects[key] = (content, contentType);
        return Task.CompletedTask;
    }

    public Task<StoredObject> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(new StoredObject(new MemoryStream(Objects[key].Data), Objects[key].Type, Objects[key].Data.Length));

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Objects.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class FixedClock(DateTime now) : IClock
{
    public DateTime UtcNow { get; set; } = now;
}

public class ArtifactServiceTests
{
    private static (ArtifactService svc, OrchestratorDbContext db, MemoryStore store) Create(long max = 1024)
    {
        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new MemoryStore();
        var svc = new ArtifactService(db, store, new FixedClock(DateTime.UtcNow),
            Options.Create(new ArtifactOptions { MaxDocumentBytes = max }));
        return (svc, db, store);
    }

    [Fact]
    public async Task Stores_object_and_records_sha256_size_and_content_type()
    {
        var (svc, db, store) = Create();
        var content = "hello document"u8.ToArray();
        var a = await svc.StoreAsync("sig_1", ArtifactType.ORIGINAL_DOCUMENT, content, "application/pdf", "c.pdf", default);
        await db.SaveChangesAsync();

        a.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        a.Size.Should().Be(content.Length);
        a.ContentType.Should().Be("application/pdf");
        a.StorageKey.Should().Be("sig_1/input/original");
        store.Objects.Should().ContainKey("sig_1/input/original");
        (await db.Artifacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Storing_the_same_type_twice_is_idempotent()
    {
        var (svc, db, _) = Create();
        var first = await svc.StoreAsync("sig_1", ArtifactType.SIGNED_DOCUMENT, [1, 2, 3], "x/y", "s", default);
        await db.SaveChangesAsync();
        var second = await svc.StoreAsync("sig_1", ArtifactType.SIGNED_DOCUMENT, [1, 2, 3], "x/y", "s", default);
        second.Id.Should().Be(first.Id);
        (await db.Artifacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Rejects_content_over_the_limit_as_permanent()
    {
        var (svc, db, store) = Create(max: 4);
        var act = () => svc.StoreAsync("sig_1", ArtifactType.ORIGINAL_DOCUMENT, new byte[5], "x/y", "c", default);
        (await act.Should().ThrowAsync<OperationException>()).Which.Transient.Should().BeFalse();
        store.Objects.Should().BeEmpty();
        (await db.Artifacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Store_failure_registers_nothing_and_is_transient()
    {
        var (svc, db, store) = Create();
        store.Fail = true;
        var act = () => svc.StoreAsync("sig_1", ArtifactType.ORIGINAL_DOCUMENT, [1], "x/y", "c", default);
        (await act.Should().ThrowAsync<OperationException>()).Which.Transient.Should().BeTrue();
        await db.SaveChangesAsync();
        (await db.Artifacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RequireStored_fails_when_object_is_missing_from_store()
    {
        var (svc, db, store) = Create();
        await svc.StoreAsync("sig_1", ArtifactType.EVIDENCE, [1], "application/json", "e", default);
        await db.SaveChangesAsync();
        store.Objects.Clear();
        var act = () => svc.RequireStoredAsync("sig_1", ArtifactType.EVIDENCE, default);
        await act.Should().ThrowAsync<OperationException>();
    }

    [Fact]
    public async Task Provider_signed_document_differs_from_original_and_evidence_is_deterministic()
    {
        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new FixedClock(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        var adapter = new FakeProviderAdapter(db, clock, Options.Create(new FakeProviderOptions { SignDelaySeconds = 1 }));
        await adapter.CreateProcessAsync(new CreateProviderProcessRequest("sig_1", "X-1", "ADVANCED", [new ProviderSigner(0, "A")]), default);
        await db.SaveChangesAsync();
        await adapter.SendDocumentAsync("sig_1", "c.pdf", default);
        await db.SaveChangesAsync();

        var early = () => adapter.DownloadSignedDocumentAsync("sig_1", [1, 2], "application/pdf", default);
        await early.Should().ThrowAsync<ProviderException>();

        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        (await adapter.GetStatusAsync("sig_1", default)).NormalizedStatus.Should().Be(ProviderStatuses.Signed);
        await db.SaveChangesAsync();

        var original = new byte[] { 1, 2, 3 };
        var s1 = await adapter.DownloadSignedDocumentAsync("sig_1", original, "application/pdf", default);
        var s2 = await adapter.DownloadSignedDocumentAsync("sig_1", original, "application/pdf", default);
        s1.Content.Should().NotEqual(original).And.Equal(s2.Content);
        s1.Content.Take(3).Should().Equal(original);
        var ev = await adapter.DownloadEvidenceAsync("sig_1", default);
        ev.ContentType.Should().Be("application/json");
        ev.Content.Should().Equal((await adapter.DownloadEvidenceAsync("sig_1", default)).Content);
    }
}

public class DownloadLinkSignerTests
{
    private static (DownloadLinkSigner signer, FixedClock clock) Create(int ttl = 300)
    {
        var clock = new FixedClock(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        return (new DownloadLinkSigner(Options.Create(new ArtifactOptions
        {
            SigningKey = "unit-test-key", PublicBaseUrl = "http://api.test", LinkTtlSeconds = ttl
        }), clock), clock);
    }

    private static (string id, long expires, string sig) Parse(string url)
    {
        var uri = new Uri(url);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return (uri.Segments[^1], long.Parse(q["expires"]!), q["sig"]!);
    }

    [Fact]
    public void Valid_link_verifies_until_expiry()
    {
        var (signer, clock) = Create();
        var (url, expiresAt) = signer.Issue("art_1");
        expiresAt.Should().Be(clock.UtcNow.AddSeconds(300));
        var (id, expires, sig) = Parse(url);
        signer.Verify(id, expires, sig).Should().Be(LinkCheck.Valid);
        clock.UtcNow = clock.UtcNow.AddSeconds(299);
        signer.Verify(id, expires, sig).Should().Be(LinkCheck.Valid);
    }

    [Fact]
    public void Expired_link_is_reported_expired()
    {
        var (signer, clock) = Create();
        var (id, expires, sig) = Parse(signer.Issue("art_1").Url);
        clock.UtcNow = clock.UtcNow.AddSeconds(301);
        signer.Verify(id, expires, sig).Should().Be(LinkCheck.Expired);
    }

    [Fact]
    public void Tampered_signature_expiry_or_artifact_is_invalid()
    {
        var (signer, _) = Create();
        var (id, expires, sig) = Parse(signer.Issue("art_1").Url);
        signer.Verify(id, expires, sig + "x").Should().Be(LinkCheck.Invalid);
        signer.Verify(id, expires + 3600, sig).Should().Be(LinkCheck.Invalid);
        signer.Verify("art_2", expires, sig).Should().Be(LinkCheck.Invalid);
        signer.Verify(id, expires, null).Should().Be(LinkCheck.Invalid);
        signer.Verify(id, expires, "").Should().Be(LinkCheck.Invalid);
    }

    [Fact]
    public void Url_has_no_storage_key_or_credentials()
    {
        var (signer, _) = Create();
        var url = signer.Issue("art_1").Url;
        url.Should().StartWith("http://api.test/v1/downloads/art_1?expires=");
        url.Should().NotContainAny("input/original", "signature-artifacts", "minioadmin", "unit-test-key", "AWSAccessKeyId");
    }

    [Fact]
    public void Different_signing_key_invalidates_the_link()
    {
        var (a, _) = Create();
        var b = new DownloadLinkSigner(Options.Create(new ArtifactOptions { SigningKey = "other" }), new FixedClock(DateTime.UtcNow));
        var (id, expires, sig) = Parse(a.Issue("art_1").Url);
        b.Verify(id, expires, sig).Should().Be(LinkCheck.Invalid);
    }
}
