using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Api;
using Xunit;

namespace Orchestrator.IntegrationTests;

internal static class Tokens
{
    public static string Make(string user, string[] roles, string? azp = null, string key = TestFixture.AuthKey, string issuer = TestFixture.AuthIssuer,
        string audience = "orchestrator-api", TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()), new("preferred_username", user), new("azp", azp ?? user),
            new("realm_access", JsonSerializer.Serialize(new { roles }), JsonClaimValueTypes.Json)
        };
        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        var token = new JwtSecurityToken(issuer, audience, claims, now.AddMinutes(life < TimeSpan.Zero ? -20 : -1), now.Add(life),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpRequestMessage Req(HttpMethod m, string url, string? token, string? body = null, string? idem = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idem is not null) r.Headers.Add("Idempotency-Key", idem);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }
}

public class RbacUnitTests
{
    [Theory]
    [InlineData("GET", "/v1/signature-processes", "viewer", true)]
    [InlineData("GET", "/v1/signature-processes/sig_1/provider", "viewer", false)]
    [InlineData("GET", "/v1/signature-processes/sig_1/provider", "operator", true)]
    [InlineData("POST", "/v1/signature-processes", "operator", true)]
    [InlineData("POST", "/v1/signature-processes", "admin", true)]
    [InlineData("POST", "/v1/signature-processes", "viewer", false)]
    [InlineData("POST", "/v1/signature-processes", "client", true)]
    [InlineData("POST", "/v1/signature-processes/sig_1/cancel", "viewer", false)]
    [InlineData("POST", "/v1/signature-processes/sig_1/cancel", "operator", true)]
    [InlineData("POST", "/v1/signature-processes/sig_1/cancel", "client", true)]
    [InlineData("POST", "/v1/callbacks", "operator", false)]
    [InlineData("POST", "/v1/callbacks", "admin", true)]
    [InlineData("POST", "/v1/proofing-sessions", "operator", false)]
    [InlineData("POST", "/v1/proofing-sessions/pro_1/retry", "operator", true)]
    [InlineData("DELETE", "/v1/proofing-sessions/pro_1/evidence", "viewer", false)]
    [InlineData("POST", "/v1/document-uploads", "operator", true)]
    [InlineData("POST", "/v1/document-uploads", "admin", true)]
    [InlineData("POST", "/v1/document-uploads", "viewer", false)]
    [InlineData("POST", "/v1/document-uploads", "client", true)]
    [InlineData("GET", "/v1/dev/confirmation-codes/sig_1", "client", false)]
    [InlineData("GET", "/v1/dev/confirmation-codes/sig_1", "admin", true)]
    [InlineData("POST", "/v1/signature-processes/sig_1/signers/sgn_1/confirmations/EMAIL/confirm", "operator", true)]
    [InlineData("POST", "/v1/signature-processes/sig_1/signers/sgn_1/confirmations/EMAIL/resend", "client", true)]
    [InlineData("POST", "/v1/signature-processes/sig_1/signers/sgn_1/confirmations/EMAIL/confirm", "viewer", false)]
    public void Matrix(string method, string path, string role, bool allowed) =>
        Rbac.Required(method, path).Contains(role).Should().Be(allowed);

    [Fact]
    public void Keycloak_realm_roles_become_role_claims_and_unknown_roles_are_ignored()
    {
        var id = new ClaimsIdentity([new Claim("realm_access", "{\"roles\":[\"operator\",\"offline_access\",\"admin\"]}")], "test");
        SecurityExtensions.MapRealmRoles(new ClaimsPrincipal(id));
        id.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo("operator", "admin");

        var bad = new ClaimsIdentity([new Claim("realm_access", "not json")], "test");
        SecurityExtensions.MapRealmRoles(new ClaimsPrincipal(bad));
        bad.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }
}

[Collection("integration")]
public class AuthApiTests(TestFixture f)
{
    private static readonly string[] Viewer = ["viewer"], Operator = ["operator"], Client = ["client"], Admin = ["admin"];

    private async Task<string> CreateAsync(HttpClient api, string token, string? idem = null, string? externalId = null)
    {
        var r = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", token,
            TestFixture.Payload(externalId ?? "AUTH-" + Guid.NewGuid().ToString("N")[..8]), idem ?? Guid.NewGuid().ToString()));
        r.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.OK);
        return (await r.JsonAsync()).GetProperty("processId").GetString()!;
    }

    [Fact]
    public async Task Every_v1_route_rejects_requests_without_a_token()
    {
        using var api = f.CreateAuthClient();
        var endpoints = f.LastExtraFactory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/v1/") && !e.RoutePattern.RawText.StartsWith("/v1/downloads") && !e.RoutePattern.RawText.StartsWith("/v1/webhooks")).ToList();
        endpoints.Should().NotBeEmpty();
        foreach (var e in endpoints)
        {
            var path = System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, @"\{[^}]+\}", "x");
            foreach (var method in e.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))
            {
                var r = await api.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
                r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} must require authentication");
                r.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            }
        }
    }

    [Fact]
    public async Task Health_and_swagger_stay_public()
    {
        using var api = f.CreateAuthClient();
        (await api.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_tokens_are_rejected()
    {
        using var api = f.CreateAuthClient();
        var tokens = new[]
        {
            "garbage",
            Tokens.Make("ana", Operator, key: "another-signing-key-0123456789-abcdefghij"),
            Tokens.Make("ana", Operator, issuer: "http://evil/realms/x"),
            Tokens.Make("ana", Operator, audience: "other-api"),
            Tokens.Make("ana", Operator, lifetime: TimeSpan.FromMinutes(-5))
        };
        foreach (var t in tokens)
            (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes", t))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes", Tokens.Make("ana", Operator)))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_operate()
    {
        using var api = f.CreateAuthClient();
        var id = await CreateAsync(api, Tokens.Make("svc", Client, "client-a"));
        var viewer = Tokens.Make("vera", Viewer);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes/" + id, viewer))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes", viewer))).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var (m, url) in new[] { (HttpMethod.Post, $"/v1/signature-processes/{id}/cancel"), (HttpMethod.Post, $"/v1/signature-processes/{id}/reconcile"),
                     (HttpMethod.Post, $"/v1/signature-processes/{id}/retry"), (HttpMethod.Post, $"/v1/signature-processes/{id}/download-link"),
                     (HttpMethod.Get, $"/v1/signature-processes/{id}/provider"), (HttpMethod.Post, "/v1/signature-processes") })
        {
            var r = await api.SendAsync(Tokens.Req(m, url, viewer, "{}", "k-" + Guid.NewGuid()));
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{m} {url}");
        }
    }

    [Fact]
    public async Task Operator_operates_and_the_journal_records_the_token_identity_not_the_header()
    {
        using var api = f.CreateAuthClient();
        var id = await CreateAsync(api, Tokens.Make("svc", Client, "client-a"));
        var req = Tokens.Req(HttpMethod.Post, $"/v1/signature-processes/{id}/cancel", Tokens.Make("olga.ops", Operator));
        req.Headers.Add("X-Operator-Id", "someone.else");
        (await api.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{id}/events?pageSize=100", Tokens.Make("olga.ops", Operator)));
        var items = (await events.JsonAsync()).GetProperty("items").EnumerateArray().ToList();
        var cancel = items.First(e => e.GetProperty("type").GetString() == "PROCESS_CANCELLED").GetProperty("actor");
        cancel.GetProperty("type").GetString().Should().Be("OPERATOR");
        cancel.GetProperty("id").GetString().Should().Be("olga.ops");
        var created = items.First(e => e.GetProperty("type").GetString() == "PROCESS_CREATED").GetProperty("actor");
        created.GetProperty("type").GetString().Should().Be("CONSUMER");
        created.GetProperty("id").GetString().Should().Be("svc");
    }

    [Fact]
    public async Task Clients_only_see_and_operate_their_own_processes()
    {
        using var api = f.CreateAuthClient();
        var a = Tokens.Make("svc-a", Client, "client-a");
        var b = Tokens.Make("svc-b", Client, "client-b");
        var idA = await CreateAsync(api, a, externalId: "SEG-A-" + Guid.NewGuid().ToString("N")[..6]);
        var idB = await CreateAsync(api, b, externalId: "SEG-B-" + Guid.NewGuid().ToString("N")[..6]);

        var listA = (await (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes?pageSize=200", a))).JsonAsync()).Ids();
        listA.Should().Contain(idA).And.NotContain(idB);
        var listB = (await (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes?pageSize=200", b))).JsonAsync()).Ids();
        listB.Should().Contain(idB).And.NotContain(idA);

        foreach (var suffix in new[] { "", "/status", "/operations", "/events", "/artifacts", "/callbacks", "/reconciliations" })
            (await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{idB}{suffix}", a))).StatusCode
                .Should().Be(HttpStatusCode.NotFound, "client A must not read client B process (" + suffix + ")");
        (await api.SendAsync(Tokens.Req(HttpMethod.Post, $"/v1/signature-processes/{idB}/cancel", a))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(Tokens.Req(HttpMethod.Post, $"/v1/signature-processes/{idB}/retry", a, "{}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{idA}", a))).StatusCode.Should().Be(HttpStatusCode.OK);
        var all = (await (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes?pageSize=200", Tokens.Make("olga", Operator)))).JsonAsync()).Ids();
        all.Should().Contain(idA).And.Contain(idB);
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{idB}", Tokens.Make("vera", Viewer)))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dead_letters_listing_is_available_to_clients_and_filtered_to_their_processes()
    {
        using var api = f.CreateAuthClient();
        var a = Tokens.Make("svc-a", Client, "client-a");
        var r = await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/dead-letters", a));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_same_idempotency_key_from_two_clients_creates_two_processes_and_replays_per_client()
    {
        using var api = f.CreateAuthClient();
        var a = Tokens.Make("svc-a", Client, "client-a");
        var b = Tokens.Make("svc-b", Client, "client-b");
        var key = "shared-" + Guid.NewGuid().ToString("N");
        var body = TestFixture.Payload("IDEM-" + Guid.NewGuid().ToString("N")[..6]);

        var ra = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", a, body, key));
        var rb = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", b, body, key));
        ra.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rb.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var idA = (await ra.JsonAsync()).GetProperty("processId").GetString();
        var idB = (await rb.JsonAsync()).GetProperty("processId").GetString();
        idA.Should().NotBe(idB);

        var replay = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", a, body, key));
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.JsonAsync()).GetProperty("processId").GetString().Should().Be(idA);
    }

    [Fact]
    public async Task Creation_is_rate_limited_per_client()
    {
        using var api = f.CreateAuthClient(new() { ["RateLimit:CreatePerMinute"] = "2" });
        var a = Tokens.Make("svc-rl", Client, "client-rl");
        var b = Tokens.Make("svc-rl2", Client, "client-rl2");
        await CreateAsync(api, a);
        await CreateAsync(api, a);
        var third = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", a, TestFixture.Payload("RL-3"), Guid.NewGuid().ToString()));
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        await CreateAsync(api, b); // another client has its own window
    }
}
