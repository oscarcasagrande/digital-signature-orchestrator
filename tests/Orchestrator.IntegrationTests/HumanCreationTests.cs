using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>
/// Starting a process from the portal: uploading the document and creating the process are allowed to client, operator and admin
/// (never viewer). People own nothing (no client), act as OPERATOR in the journal and are isolated from API clients.
/// </summary>
[Collection("integration")]
public class HumanCreationTests(TestFixture f)
{
    private static readonly string[] Viewer = ["viewer"], Operator = ["operator"], Client = ["client"], Admin = ["admin"];

    private static HttpRequestMessage Upload(string? token)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestFixture.DocBytes);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", "contract.pdf");
        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/document-uploads") { Content = form };
        if (token is not null) r.Headers.Authorization = new("Bearer", token);
        return r;
    }

    private static string PortalBody(string uploadId) =>
        "{\"externalId\":\"PORTAL-" + Guid.NewGuid().ToString("N")[..8] + "\",\"document\":{\"fileName\":\"contract.pdf\",\"source\":{\"type\":\"UPLOAD\",\"uploadId\":\"" + uploadId + "\"}},"
        + "\"defaults\":{\"signatureType\":\"ADVANCED\"},\"signers\":[{\"name\":\"Maria Souza\",\"document\":\"12345678909\",\"email\":\"maria@example.com\"}]}";

    private static async Task<string> UploadIdAsync(HttpClient api, string token)
    {
        var r = await api.SendAsync(Upload(token));
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // ---- role matrix -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("client", HttpStatusCode.Created)]
    [InlineData("operator", HttpStatusCode.Created)]
    [InlineData("admin", HttpStatusCode.Created)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("none", HttpStatusCode.Unauthorized)]
    public async Task Document_upload_role_matrix(string role, HttpStatusCode expected)
    {
        using var api = f.CreateAuthClient();
        var token = role == "none" ? null : Tokens.Make("user-" + role, [role]);
        (await api.SendAsync(Upload(token))).StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("client", HttpStatusCode.Accepted)]
    [InlineData("operator", HttpStatusCode.Accepted)]
    [InlineData("admin", HttpStatusCode.Accepted)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("none", HttpStatusCode.Unauthorized)]
    public async Task Process_creation_role_matrix(string role, HttpStatusCode expected)
    {
        using var api = f.CreateAuthClient();
        var token = role == "none" ? null : Tokens.Make("user-" + role, [role]);
        var r = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", token, TestFixture.Payload("MATRIX-" + Guid.NewGuid().ToString("N")[..6]), Guid.NewGuid().ToString()));
        r.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.Forbidden) (await r.Content.ReadAsStringAsync()).Should().Contain("Forbidden");
    }

    [Fact]
    public async Task Other_routes_keep_their_roles_operator_still_cannot_register_callbacks_or_open_proofing_sessions()
    {
        using var api = f.CreateAuthClient();
        var op = Tokens.Make("olga", Operator);
        (await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/callbacks", op, "{}"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/proofing-sessions", op, "{}", Guid.NewGuid().ToString()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- the portal path: upload then create with the same token ---------------------------------------------------------------

    [Fact]
    public async Task An_operator_creates_a_process_through_the_portal_path_and_is_recorded_as_the_actor()
    {
        using var api = f.CreateAuthClient();
        var token = Tokens.Make("olga.ops", Operator);
        var uploadId = await UploadIdAsync(api, token);
        var create = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", token, PortalBody(uploadId), Guid.NewGuid().ToString()));
        create.StatusCode.Should().Be(HttpStatusCode.Accepted, await create.Content.ReadAsStringAsync());
        var id = (await JsonAsync(create)).GetProperty("processId").GetString()!;

        var events = (await JsonAsync(await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{id}/events?pageSize=200", token)))).GetProperty("items");
        var created = events.EnumerateArray().First(e => e.GetProperty("type").GetString() == "PROCESS_CREATED");
        created.GetProperty("actor").GetProperty("type").GetString().Should().Be("OPERATOR");
        created.GetProperty("actor").GetProperty("id").GetString().Should().Be("olga.ops");

        // the process runs to completion like any other
        var deadline = DateTime.UtcNow.AddSeconds(45);
        string status;
        do
        {
            status = (await JsonAsync(await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{id}/status", token)))).GetProperty("businessStatus").GetString()!;
            if (status == "COMPLETED") break;
            await Task.Delay(300);
        } while (DateTime.UtcNow < deadline);
        status.Should().Be("COMPLETED");
    }

    [Fact]
    public async Task An_admin_is_recorded_as_the_actor_too()
    {
        using var api = f.CreateAuthClient();
        var token = Tokens.Make("root", Admin);
        var r = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", token, TestFixture.Payload("ADM-" + Guid.NewGuid().ToString("N")[..6]), Guid.NewGuid().ToString()));
        var id = (await JsonAsync(r)).GetProperty("processId").GetString()!;
        var events = (await JsonAsync(await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{id}/events", token)))).GetProperty("items");
        events[0].GetProperty("actor").GetProperty("id").GetString().Should().Be("root");
        events[0].GetProperty("actor").GetProperty("type").GetString().Should().Be("OPERATOR");
    }

    // ---- ownership and segregation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Processes_created_by_people_have_no_client_owner_so_clients_never_see_them_while_staff_sees_everything()
    {
        using var api = f.CreateAuthClient();
        var op = Tokens.Make("olga", Operator);
        var a = Tokens.Make("svc-a", Client, "client-a");
        var humanId = (await JsonAsync(await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", op,
            TestFixture.Payload("HUMAN-" + Guid.NewGuid().ToString("N")[..6]), Guid.NewGuid().ToString())))).GetProperty("processId").GetString()!;
        var clientId = (await JsonAsync(await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", a,
            TestFixture.Payload("CLI-" + Guid.NewGuid().ToString("N")[..6]), Guid.NewGuid().ToString())))).GetProperty("processId").GetString()!;

        // the client cannot reach the human-created process (404, same as a process that does not exist), nor list it
        (await api.SendAsync(Tokens.Req(HttpMethod.Get, $"/v1/signature-processes/{humanId}", a))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(Tokens.Req(HttpMethod.Post, $"/v1/signature-processes/{humanId}/cancel", a))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var listed = await (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes?pageSize=200", a))).Content.ReadAsStringAsync();
        listed.Should().Contain(clientId).And.NotContain(humanId);

        // operator, admin and viewer see both
        foreach (var token in new[] { op, Tokens.Make("root", Admin), Tokens.Make("vera", Viewer) })
        {
            var all = await (await api.SendAsync(Tokens.Req(HttpMethod.Get, "/v1/signature-processes?pageSize=200", token))).Content.ReadAsStringAsync();
            all.Should().Contain(humanId).And.Contain(clientId);
        }
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_person_so_two_operators_never_replay_each_other()
    {
        using var api = f.CreateAuthClient();
        var key = "shared-key-" + Guid.NewGuid().ToString("N")[..8];
        var olga = Tokens.Make("olga", Operator);
        var otto = Tokens.Make("otto", Operator);
        var body = TestFixture.Payload("IDEM-" + Guid.NewGuid().ToString("N")[..6]);

        var first = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", olga, body, key));
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstId = (await JsonAsync(first)).GetProperty("processId").GetString();
        var replay = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", olga, body, key));
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(replay)).GetProperty("processId").GetString().Should().Be(firstId);

        var other = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", otto, body, key));
        other.StatusCode.Should().Be(HttpStatusCode.Accepted, "another person with the same key creates a new process");
        (await JsonAsync(other)).GetProperty("processId").GetString().Should().NotBe(firstId);
    }

    [Fact]
    public async Task Uploads_follow_the_same_ownership_clients_use_their_own_people_use_any_and_people_uploads_are_not_for_clients()
    {
        using var api = f.CreateAuthClient();
        var op = Tokens.Make("olga", Operator);
        var admin = Tokens.Make("root", Admin);
        var a = Tokens.Make("svc-a", Client, "client-a");
        var humanUpload = await UploadIdAsync(api, op);
        var clientUpload = await UploadIdAsync(api, a);

        async Task<HttpStatusCode> Create(string token, string upload) =>
            (await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", token, PortalBody(upload), Guid.NewGuid().ToString()))).StatusCode;

        (await Create(op, humanUpload)).Should().Be(HttpStatusCode.Accepted);
        (await Create(admin, humanUpload)).Should().Be(HttpStatusCode.Accepted); // people share the staff scope
        (await Create(a, humanUpload)).Should().Be(HttpStatusCode.BadRequest, "a client cannot use a document uploaded by a person");
        (await Create(a, clientUpload)).Should().Be(HttpStatusCode.Accepted);
        (await Create(op, clientUpload)).Should().Be(HttpStatusCode.Accepted, "staff may start a process from a client document");
        (await Create(Tokens.Make("svc-b", Client, "client-b"), clientUpload)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_viewer_cannot_create_even_with_a_valid_upload_made_by_someone_else()
    {
        using var api = f.CreateAuthClient();
        var upload = await UploadIdAsync(api, Tokens.Make("olga", Operator));
        var r = await api.SendAsync(Tokens.Req(HttpMethod.Post, "/v1/signature-processes", Tokens.Make("vera", Viewer), PortalBody(upload), Guid.NewGuid().ToString()));
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
