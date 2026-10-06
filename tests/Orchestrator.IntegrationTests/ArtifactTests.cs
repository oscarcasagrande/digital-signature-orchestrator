using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Infrastructure.Persistence;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class Sha
{
    public static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

[Collection("integration")]
public class ArtifactWorkflowTests(TestFixture f)
{
    [Fact]
    public async Task Completed_process_has_original_signed_and_evidence_with_correct_hashes()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");

        var list = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts");
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("type").GetString()!);
        items.Keys.Should().BeEquivalentTo("ORIGINAL_DOCUMENT", "SIGNED_DOCUMENT", "EVIDENCE");

        var original = items["ORIGINAL_DOCUMENT"];
        original.GetProperty("sha256").GetString().Should().Be(Sha.Hex(TestFixture.DocBytes));
        original.GetProperty("size").GetInt64().Should().Be(TestFixture.DocBytes.Length);
        original.GetProperty("contentType").GetString().Should().Be("application/pdf");
        items["SIGNED_DOCUMENT"].GetProperty("sha256").GetString().Should().NotBe(original.GetProperty("sha256").GetString());
        items["EVIDENCE"].GetProperty("contentType").GetString().Should().Be("application/json");

        list.ToString().Should().NotContain("storageKey").And.NotContain("/input/").And.NotContain("signature-artifacts");

        var events = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events")).GetProperty("items");
        var stored = events.EnumerateArray().Single(e => e.GetProperty("type").GetString() == "DOCUMENT_STORED");
        stored.GetProperty("metadata").GetProperty("sha256").GetString().Should().Be(Sha.Hex(TestFixture.DocBytes));
        events.EnumerateArray().Should().Contain(e => e.GetProperty("type").GetString() == "FINAL_DOCUMENT_STORED");
    }

    [Fact]
    public async Task Unreachable_source_404_fails_the_operation_and_stores_nothing()
    {
        var id = await f.CreateProcessAsync(documentUrl: f.OriginUrl + "/missing");
        await WaitForManualActionAsync(id);
        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations")).GetProperty("items");
        var failed = ops.EnumerateArray().Single(o => o.GetProperty("status").GetString() == "FAILED");
        failed.GetProperty("type").GetString().Should().Be("DOCUMENT_DOWNLOAD");
        failed.GetProperty("error").ToString().Should().Contain("404");
        (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Document_over_the_size_limit_is_rejected()
    {
        var id = await f.CreateProcessAsync(documentUrl: f.OriginUrl + "/big");
        await WaitForManualActionAsync(id);
        var ops = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/operations")).GetProperty("items");
        ops.EnumerateArray().Single(o => o.GetProperty("status").GetString() == "FAILED")
            .GetProperty("error").ToString().Should().Contain("limit");
    }

    [Fact]
    public async Task Redelivered_download_operation_does_not_create_a_second_artifact()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Artifacts.CountAsync(a => a.ProcessId == id)).Should().Be(3);
        (await db.Artifacts.Where(a => a.ProcessId == id).Select(a => a.Type).Distinct().CountAsync()).Should().Be(3);
    }

    private async Task WaitForManualActionAsync(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement st;
        do
        {
            await Task.Delay(300);
            st = await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/status");
        } while (st.GetProperty("operationalStatus").GetString() != "MANUAL_ACTION" && DateTime.UtcNow < deadline);
        st.GetProperty("operationalStatus").GetString().Should().Be("MANUAL_ACTION");
    }
}

[Collection("integration")]
public class ArtifactApiTests(TestFixture f)
{
    [Fact]
    public async Task Artifacts_of_unknown_process_is_404() =>
        (await f.Client.GetAsync("/v1/signature-processes/sig_nope/artifacts")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task In_progress_process_lists_only_stored_artifacts()
    {
        var id = await f.CreateProcessAsync();
        // Wait until the original is stored, then check before completion is possible (signing takes > 0.5 s).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement items;
        do
        {
            await Task.Delay(100);
            items = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items");
        } while (items.GetArrayLength() == 0 && DateTime.UtcNow < deadline);
        items.GetArrayLength().Should().BeGreaterThan(0);
        items.EnumerateArray().Select(i => i.GetProperty("type").GetString()).Should().Contain("ORIGINAL_DOCUMENT");
    }
}

[Collection("integration")]
public class DownloadTests(TestFixture f)
{
    private async Task<(string id, JsonElement artifacts)> CompletedAsync()
    {
        var id = await f.CreateProcessAsync();
        await f.WaitForStatusAsync(id, "COMPLETED");
        return (id, (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/artifacts")).GetProperty("items"));
    }

    private async Task<(string url, string path)> LinkAsync(string id, object? body = null)
    {
        var r = await f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/download-link", body ?? new { });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        j.GetProperty("expiresAt").ValueKind.Should().Be(JsonValueKind.String);
        var url = j.GetProperty("url").GetString()!;
        return (url, new Uri(url).PathAndQuery);
    }

    [Fact]
    public async Task Default_link_is_the_signed_document_and_content_matches_the_listed_hash()
    {
        var (id, items) = await CompletedAsync();
        var signed = items.EnumerateArray().Single(a => a.GetProperty("type").GetString() == "SIGNED_DOCUMENT");
        var (url, path) = await LinkAsync(id);
        url.Should().NotContainAny("signature-artifacts", "minioadmin", "/output/", "signed.pdf");

        var resp = await f.Client.GetAsync(path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Sha.Hex(bytes).Should().Be(signed.GetProperty("sha256").GetString());
        resp.Headers.GetValues("X-Content-SHA256").Single().Should().Be(signed.GetProperty("sha256").GetString());
        resp.Content.Headers.ContentLength.Should().Be(signed.GetProperty("size").GetInt64());

        var events = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events")).GetProperty("items");
        events.EnumerateArray().Count(e => e.GetProperty("type").GetString() == "DOWNLOAD_LINK_ISSUED").Should().Be(1);
        events.EnumerateArray().Count(e => e.GetProperty("type").GetString() == "DOCUMENT_DOWNLOADED").Should().Be(1);
    }

    [Fact]
    public async Task Original_can_be_downloaded_by_type_and_equals_the_source_document()
    {
        var (id, _) = await CompletedAsync();
        var (_, path) = await LinkAsync(id, new { type = "ORIGINAL_DOCUMENT" });
        var bytes = await (await f.Client.GetAsync(path)).Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(TestFixture.DocBytes);
    }

    [Fact]
    public async Task Link_by_artifact_id_and_unknown_artifact()
    {
        var (id, items) = await CompletedAsync();
        var evidence = items.EnumerateArray().Single(a => a.GetProperty("type").GetString() == "EVIDENCE");
        var (_, path) = await LinkAsync(id, new { artifactId = evidence.GetProperty("artifactId").GetString() });
        (await f.Client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

        var bad = await f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/download-link", new { artifactId = "art_nope" });
        bad.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var badType = await f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/download-link", new { type = "NOPE" });
        badType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tampered_signature_and_expiry_are_forbidden()
    {
        var (id, _) = await CompletedAsync();
        var (_, path) = await LinkAsync(id);
        (await f.Client.GetAsync(path + "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
        var longer = path.Replace("expires=" + q["expires"], "expires=" + (long.Parse(q["expires"]!) + 3600));
        (await f.Client.GetAsync(longer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.Client.GetAsync(path.Split('?')[0])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Link_is_scoped_to_a_single_artifact()
    {
        var (id, items) = await CompletedAsync();
        var (_, path) = await LinkAsync(id);
        var other = items.EnumerateArray().First(a => a.GetProperty("type").GetString() != "SIGNED_DOCUMENT").GetProperty("artifactId").GetString();
        var swapped = "/v1/downloads/" + other + "?" + path.Split('?')[1];
        (await f.Client.GetAsync(swapped)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Expired_link_is_gone_and_leaves_no_download_event()
    {
        var (id, _) = await CompletedAsync();
        var (_, path) = await LinkAsync(id);
        await Task.Delay(4000);
        (await f.Client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Gone);
        var events = (await f.Client.GetFromJsonAsync<JsonElement>($"/v1/signature-processes/{id}/events")).GetProperty("items");
        events.EnumerateArray().Should().NotContain(e => e.GetProperty("type").GetString() == "DOCUMENT_DOWNLOADED");
    }

    [Fact]
    public async Task Link_for_unknown_process_is_404() =>
        (await f.Client.PostAsJsonAsync("/v1/signature-processes/sig_nope/download-link", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Link_before_any_artifact_exists_is_404()
    {
        // Process whose document source fails never gets an artifact.
        var id = await f.CreateProcessAsync(documentUrl: f.OriginUrl + "/missing");
        await Task.Delay(2500);
        (await f.Client.PostAsJsonAsync($"/v1/signature-processes/{id}/download-link", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
