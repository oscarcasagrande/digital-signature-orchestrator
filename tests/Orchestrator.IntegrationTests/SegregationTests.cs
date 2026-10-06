using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>Cross-client access: a client must get 404 for processes, proofing sessions, callbacks and artifacts it does not own.</summary>
[Collection("integration")]
public class SegregationTests(TestFixture f)
{
    private static readonly string[] Client = ["client"], Operator = ["operator"], Admin = ["admin"];
    private static readonly string A = Tokens.Make("svc-a", Client, "client-a");
    private static readonly string B = Tokens.Make("svc-b", Client, "client-b");

    private static async Task<HttpResponseMessage> Send(HttpClient api, HttpMethod m, string url, string token, string? body = null, string? idem = null) =>
        await api.SendAsync(Tokens.Req(m, url, token, body, idem));

    private static async Task<string> CreateSession(HttpClient api, string token, string? key = null, string? external = null)
    {
        var r = await Send(api, HttpMethod.Post, "/v1/proofing-sessions", token,
            ProofingApi.Body(external ?? "SEG-KYC-" + Guid.NewGuid().ToString("N")[..6], "[{\"type\":\"PERSON_DATA\",\"required\":true},{\"type\":\"FACE_MATCH\",\"required\":true}]"),
            key ?? Guid.NewGuid().ToString());
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return (await r.JsonAsync()).GetProperty("sessionId").GetString()!;
    }

    private static async Task<string> CreateProcess(HttpClient api, string token) =>
        (await (await Send(api, HttpMethod.Post, "/v1/signature-processes", token,
            TestFixture.Payload("SEG-" + Guid.NewGuid().ToString("N")[..6]), Guid.NewGuid().ToString())).JsonAsync()).GetProperty("processId").GetString()!;

    private static async Task<string> Register(HttpClient api, string token, string id, string url) =>
        (await (await Send(api, HttpMethod.Post, "/v1/callbacks", token, JsonSerializer.Serialize(new { callbackId = id, url }))).JsonAsync()).GetProperty("callbackId").GetString()!;

    private static async Task Wait(Func<Task<bool>> done, int seconds = 45)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (await done()) return; await Task.Delay(250); }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Proofing_sessions_belong_to_the_client_that_created_them()
    {
        using var api = f.CreateAuthClient();
        var id = await CreateSession(api, A);
        var ev = Convert.ToBase64String(Encoding.UTF8.GetBytes("selfie"));
        var upload = JsonSerializer.Serialize(new { type = "SELFIE", contentType = "image/jpeg", content = ev });
        (await Send(api, HttpMethod.Post, $"/v1/proofing-sessions/{id}/biometrics", A, upload)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        foreach (var suffix in new[] { "", "/result", "/operations", "/events" })
        {
            (await Send(api, HttpMethod.Get, $"/v1/proofing-sessions/{id}{suffix}", B)).StatusCode.Should().Be(HttpStatusCode.NotFound, suffix);
            (await Send(api, HttpMethod.Get, $"/v1/proofing-sessions/{id}{suffix}", A)).StatusCode.Should().Be(HttpStatusCode.OK, suffix);
            (await Send(api, HttpMethod.Get, $"/v1/proofing-sessions/{id}{suffix}", Tokens.Make("olga", Operator))).StatusCode.Should().Be(HttpStatusCode.OK, suffix);
        }
        (await Send(api, HttpMethod.Post, $"/v1/proofing-sessions/{id}/documents", B,
            JsonSerializer.Serialize(new { type = "DOCUMENT_FRONT", contentType = "image/jpeg", content = ev }))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Post, $"/v1/proofing-sessions/{id}/biometrics", B, upload)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Post, $"/v1/proofing-sessions/{id}/retry", B, "{}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Delete, $"/v1/proofing-sessions/{id}/evidence", B)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Proofing_idempotency_keys_are_per_client()
    {
        using var api = f.CreateAuthClient();
        var key = "shared-" + Guid.NewGuid().ToString("N");
        var a = await CreateSession(api, A, key, "SEG-IDEM-1");
        var b = await CreateSession(api, B, key, "SEG-IDEM-2");
        a.Should().NotBe(b);
        var replay = await Send(api, HttpMethod.Post, "/v1/proofing-sessions", A,
            ProofingApi.Body("SEG-IDEM-1", "[{\"type\":\"PERSON_DATA\",\"required\":true},{\"type\":\"FACE_MATCH\",\"required\":true}]"), key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.JsonAsync()).GetProperty("sessionId").GetString().Should().Be(a);
    }

    [Fact]
    public async Task Registered_callbacks_are_private_to_their_owner()
    {
        using var api = f.CreateAuthClient();
        var url = f.CallbackUrl("seg-" + Guid.NewGuid().ToString("N")[..6]);
        var idA = await Register(api, A, "seg-a-" + Guid.NewGuid().ToString("N")[..8], url);

        (await Send(api, HttpMethod.Get, $"/v1/callbacks/{idA}", B)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Delete, $"/v1/callbacks/{idA}", B)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Get, $"/v1/callbacks/{idA}", A)).StatusCode.Should().Be(HttpStatusCode.OK);

        var listB = await (await Send(api, HttpMethod.Get, "/v1/callbacks", B)).JsonAsync();
        listB.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("callbackId").GetString()).Should().NotContain(idA);
        var listA = await (await Send(api, HttpMethod.Get, "/v1/callbacks", A)).JsonAsync();
        listA.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("callbackId").GetString()).Should().Contain(idA);
        var listAdmin = await (await Send(api, HttpMethod.Get, "/v1/callbacks", Tokens.Make("root", Admin))).JsonAsync();
        listAdmin.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("callbackId").GetString()).Should().Contain(idA);

        // B cannot use A callback in a process, and A callback stays active.
        var cb = "{\"callbackId\":\"" + idA + "\"}";
        var denied = await Send(api, HttpMethod.Post, "/v1/signature-processes", B,
            TestFixture.Payload("SEG-CB-" + Guid.NewGuid().ToString("N")[..6], callbackJson: cb), Guid.NewGuid().ToString());
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("Unknown or inactive callbackId");
        var allowed = await Send(api, HttpMethod.Post, "/v1/signature-processes", A,
            TestFixture.Payload("SEG-CB-" + Guid.NewGuid().ToString("N")[..6], callbackJson: cb), Guid.NewGuid().ToString());
        allowed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await (await Send(api, HttpMethod.Get, $"/v1/callbacks/{idA}", A)).JsonAsync()).GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Callbacks_registered_by_an_admin_are_shared_for_use_but_not_listed_to_clients()
    {
        using var api = f.CreateAuthClient();
        var id = await Register(api, Tokens.Make("root", Admin), "seg-shared-" + Guid.NewGuid().ToString("N")[..8], f.CallbackUrl("shared"));
        var ok = await Send(api, HttpMethod.Post, "/v1/signature-processes", B,
            TestFixture.Payload("SEG-SH-" + Guid.NewGuid().ToString("N")[..6], callbackJson: "{\"callbackId\":\"" + id + "\"}"), Guid.NewGuid().ToString());
        ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Send(api, HttpMethod.Get, $"/v1/callbacks/{id}", B)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Artifacts_and_download_links_are_scoped_to_the_owner_process()
    {
        using var api = f.CreateAuthClient();
        var pa = await CreateProcess(api, A);
        var pb = await CreateProcess(api, B);
        string artifactA = "";
        await Wait(async () =>
        {
            var items = (await (await Send(api, HttpMethod.Get, $"/v1/signature-processes/{pa}/artifacts", A)).JsonAsync()).GetProperty("items").EnumerateArray().ToList();
            artifactA = items.FirstOrDefault(i => i.GetProperty("type").GetString() == "ORIGINAL_DOCUMENT").ValueKind == JsonValueKind.Object
                ? items.First(i => i.GetProperty("type").GetString() == "ORIGINAL_DOCUMENT").GetProperty("artifactId").GetString()! : "";
            return artifactA != "";
        });
        await Wait(async () =>
            (await Send(api, HttpMethod.Get, $"/v1/signature-processes/{pb}/artifacts", B)).Content.ReadAsStringAsync().Result.Contains("ORIGINAL_DOCUMENT"));

        (await Send(api, HttpMethod.Get, $"/v1/signature-processes/{pa}/artifacts", B)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(api, HttpMethod.Post, $"/v1/signature-processes/{pa}/download-link", B, "{}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // A foreign artifact id on the caller own process is never honoured either.
        var cross = await Send(api, HttpMethod.Post, $"/v1/signature-processes/{pb}/download-link", B, JsonSerializer.Serialize(new { artifactId = artifactA }));
        cross.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The owner gets a link that works (the signed URL is the capability, issued only to the owner).
        var link = await Send(api, HttpMethod.Post, $"/v1/signature-processes/{pa}/download-link", A, JsonSerializer.Serialize(new { artifactId = artifactA }));
        link.StatusCode.Should().Be(HttpStatusCode.OK);
        var url = new Uri((await link.JsonAsync()).GetProperty("url").GetString()!);
        (await api.GetAsync(url.PathAndQuery)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_resource_endpoint_answers_404_to_a_client_that_does_not_own_the_resource()
    {
        using var api = f.CreateAuthClient();
        var process = await CreateProcess(api, A);
        var session = await CreateSession(api, A);
        var callback = await Register(api, A, "seg-sweep-" + Guid.NewGuid().ToString("N")[..8], f.CallbackUrl("sweep"));
        var ids = new Dictionary<string, string> { ["signature-processes"] = process, ["proofing-sessions"] = session, ["callbacks"] = callback };

        var endpoints = f.LastExtraFactory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => (Pattern: e.RoutePattern.RawText!, Methods: e.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).ToList()))
            .Where(e => ids.Keys.Any(k => e.Pattern.StartsWith($"/v1/{k}/{{"))).ToList();
        endpoints.Count.Should().BeGreaterThan(20, "the sweep must cover the whole resource surface");

        foreach (var (pattern, methods) in endpoints)
        {
            var prefix = ids.Keys.First(k => pattern.StartsWith($"/v1/{k}/{{"));
            var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}/]+\}", ids[prefix], System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            // only the first parameter is the resource id; the rest of the route has literal segments
            foreach (var method in methods)
            {
                var req = Tokens.Req(new HttpMethod(method), path, B, method is "POST" or "PUT" or "PATCH" or "DELETE" ? "{}" : null, Guid.NewGuid().ToString());
                var r = await api.SendAsync(req);
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {pattern} must not reveal resources of another client");
            }
        }
    }
}
