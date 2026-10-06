using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Orchestrator.IntegrationTests;

/// <summary>
/// In-process stand-ins for the DocuSign eSignature and Lacuna Signer REST APIs (only the calls the adapters make), so the
/// adapters, webhooks and the whole workflow are tested without credentials. They are passive: tests drive "what the signers did".
/// </summary>
public static class FakeVendors
{
    public static readonly RSA SigningKey = RSA.Create(2048);
    public static string PrivatePem => SigningKey.ExportPkcs8PrivateKeyPem();
    public const string DsToken = "fake-docusign-access-token", LzApiKey = "fake-app|fake-key";
    public const string ConnectKey = "fake-connect-hmac-key", LacunaSecret = "fake-lacuna-webhook-secret";

    // ---- DocuSign ------------------------------------------------------------------------------------------------------------
    public sealed class DsSigner { public string RecipientId = "", Email = "", Name = "", RoutingOrder = "1", Status = "created"; }
    public sealed class DsEnvelope
    {
        public string Id = "", Status = "created", Ref = "", FileName = "";
        public byte[] Document = [];
        public List<DsSigner> Signers = [];
    }

    public static readonly ConcurrentDictionary<string, DsEnvelope> Envelopes = new();
    public static int DsTokenRequests, DsCreateCalls, DsAuthFailures;
    public static readonly ConcurrentQueue<string> DsAssertions = new();

    public static DsEnvelope? DsByRef(string reference) => Envelopes.Values.FirstOrDefault(e => e.Ref == reference);
    public static List<DsEnvelope> DsAllByRef(string reference) => Envelopes.Values.Where(e => e.Ref == reference).ToList();
    public static void DsSign(string id, params int[] positions)
    {
        var e = Envelopes[id];
        foreach (var s in e.Signers.Where(s => positions.Contains(int.Parse(s.RecipientId) - 1))) s.Status = "completed";
    }
    public static void DsComplete(string id)
    {
        var e = Envelopes[id];
        foreach (var s in e.Signers) s.Status = "completed";
        e.Status = "completed";
    }

    // ---- Lacuna --------------------------------------------------------------------------------------------------------------
    public sealed class LzAction { public string Status = "Pending", Name = "", Email = "", Identifier = ""; public int Step; }
    public sealed class LzDocument
    {
        public string Id = "", Status = "Pending", Description = "", CancelReason = "";
        public byte[] Content = [];
        public List<LzAction> Actions = [];
    }

    public static readonly ConcurrentDictionary<string, byte[]> Uploads = new();
    public static readonly ConcurrentDictionary<string, LzDocument> Documents = new();
    public static LzDocument? LzByRef(string reference) => Documents.Values.FirstOrDefault(d => d.Description == "orchestrator:" + reference);
    public static void LzComplete(string id)
    {
        var d = Documents[id];
        foreach (var a in d.Actions) a.Status = "Completed";
        d.Status = "Concluded";
    }

    // ---- failure injection: key -> remaining failures (status code decided by the key's second element) --------------------------
    private static readonly ConcurrentDictionary<string, (int Left, int Status)> Failures = new();
    public static void Fail(string key, int times, int status) => Failures[key] = (times, status);
    private static IResult? Injected(string key)
    {
        if (!Failures.TryGetValue(key, out var f) || f.Left <= 0) return null;
        Failures[key] = (f.Left - 1, f.Status);
        return Results.StatusCode(f.Status);
    }

    private static bool DsAuthorized(HttpRequest r)
    {
        var ok = r.Headers.Authorization.ToString() == "Bearer " + DsToken;
        if (!ok) Interlocked.Increment(ref DsAuthFailures);
        return ok;
    }

    public static void Map(WebApplication app)
    {
        // OAuth JWT Grant: the assertion must be an RS256 JWT signed by our key with the integration claims.
        app.MapPost("/docusign/oauth/token", async (HttpRequest r) =>
        {
            Interlocked.Increment(ref DsTokenRequests);
            var form = await r.ReadFormAsync();
            var assertion = form["assertion"].ToString();
            DsAssertions.Enqueue(assertion);
            if (form["grant_type"] != "urn:ietf:params:oauth:grant-type:jwt-bearer") return Results.BadRequest(new { error = "unsupported_grant_type" });
            var parts = assertion.Split('.');
            if (parts.Length != 3) return Results.BadRequest(new { error = "invalid_grant" });
            static byte[] Dec(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
            var valid = SigningKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Dec(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var claims = JsonNode.Parse(Dec(parts[1]));
            valid &= JsonNode.Parse(Dec(parts[0]))?["alg"]?.GetValue<string>() == "RS256";
            valid &= claims?["iss"]?.GetValue<string>() == "test-ik" && claims?["sub"]?.GetValue<string>() == "test-user";
            valid &= claims?["scope"]?.GetValue<string>() == "signature impersonation";
            valid &= claims?["exp"]?.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return valid ? Results.Json(new { access_token = DsToken, token_type = "Bearer", expires_in = 3600 }) : Results.BadRequest(new { error = "invalid_grant" });
        });

        var ds = app.MapGroup("/docusign/restapi/v2.1/accounts/ACC");

        ds.MapPost("/envelopes", async (HttpRequest r) =>
        {
            if (!DsAuthorized(r)) return Results.Unauthorized();
            if (Injected("ds:create") is { } f) return f;
            Interlocked.Increment(ref DsCreateCalls);
            var body = JsonNode.Parse(await new StreamReader(r.Body).ReadToEndAsync())!;
            var signers = body["recipients"]!["signers"]!.AsArray();
            if (signers.Any(s => s!["tabs"]?["signHereTabs"] is null) || body["status"]!.GetValue<string>() != "created")
                return Results.BadRequest(new { errorCode = "INVALID_REQUEST_BODY" });
            var env = new DsEnvelope
            {
                Id = Guid.NewGuid().ToString(), Status = "created", FileName = body["documents"]![0]!["name"]!.GetValue<string>(),
                Document = Convert.FromBase64String(body["documents"]![0]!["documentBase64"]!.GetValue<string>()),
                Ref = body["customFields"]!["textCustomFields"]![0]!["value"]!.GetValue<string>(),
                Signers = signers.Select(s => new DsSigner
                {
                    RecipientId = s!["recipientId"]!.GetValue<string>(), Email = s["email"]!.GetValue<string>(), Name = s["name"]!.GetValue<string>(),
                    RoutingOrder = s["routingOrder"]!.GetValue<string>()
                }).ToList()
            };
            Envelopes[env.Id] = env;
            return Results.Json(new { envelopeId = env.Id, status = "created" }, statusCode: 201);
        });

        ds.MapGet("/envelopes", (HttpRequest r) =>
        {
            if (!DsAuthorized(r)) return Results.Unauthorized();
            var cf = r.Query["custom_field"].ToString();
            var reference = cf.StartsWith("orchestratorRef=") ? cf["orchestratorRef=".Length..] : "";
            var found = Envelopes.Values.Where(e => e.Ref == reference).Select(e => new { envelopeId = e.Id, status = e.Status }).ToList();
            return Results.Json(new { resultSetSize = found.Count.ToString(), envelopes = found.Count == 0 ? null : found });
        });

        ds.MapGet("/envelopes/{id}", (string id, HttpRequest r) =>
        {
            if (!DsAuthorized(r)) return Results.Unauthorized();
            if (Injected("ds:get") is { } f) return f;
            if (!Envelopes.TryGetValue(id, out var e)) return Results.NotFound(new { errorCode = "ENVELOPE_DOES_NOT_EXIST" });
            object recipients = new { signers = e.Signers.Select(s => new { recipientId = s.RecipientId, email = s.Email, name = s.Name, routingOrder = s.RoutingOrder, status = s.Status }).ToList() };
            return r.Query["include"].ToString().Contains("recipients") ? Results.Json(new { envelopeId = e.Id, status = e.Status, recipients })
                : Results.Json(new { envelopeId = e.Id, status = e.Status });
        });

        ds.MapPut("/envelopes/{id}", async (string id, HttpRequest r) =>
        {
            if (!DsAuthorized(r)) return Results.Unauthorized();
            if (!Envelopes.TryGetValue(id, out var e)) return Results.NotFound();
            var status = JsonNode.Parse(await new StreamReader(r.Body).ReadToEndAsync())?["status"]?.GetValue<string>();
            if (status == "sent" && e.Status == "created") { e.Status = "sent"; foreach (var s in e.Signers) s.Status = "sent"; }
            else if (status == "voided" && e.Status is "sent" or "delivered") e.Status = "voided";
            else return Results.BadRequest(new { errorCode = "ENVELOPE_INVALID_STATUS" });
            return Results.Json(new { envelopeId = e.Id, status = e.Status });
        });

        ds.MapGet("/envelopes/{id}/documents/{doc}", (string id, string doc, HttpRequest r) =>
        {
            if (!DsAuthorized(r)) return Results.Unauthorized();
            if (!Envelopes.TryGetValue(id, out var e)) return Results.NotFound();
            return doc switch
            {
                "combined" when e.Status == "completed" => Results.File(e.Document.Concat(Encoding.ASCII.GetBytes("\n%%DOCUSIGN-SIGNED\n")).ToArray(), "application/pdf"),
                "certificate" => Results.File(Encoding.ASCII.GetBytes("%PDF-1.4 certificate of completion " + id), "application/pdf"),
                _ => Results.NotFound()
            };
        });

        // ---- Lacuna Signer --------------------------------------------------------------------------------------------------
        var lz = app.MapGroup("/lacuna/api");
        lz.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Headers["X-Api-Key"] == LzApiKey ? await next(ctx) : Results.Unauthorized());

        lz.MapPost("/uploads", async (HttpRequest r) =>
        {
            if (Injected("lz:upload") is { } f) return f;
            using var ms = new MemoryStream();
            await r.Body.CopyToAsync(ms);
            var id = Guid.NewGuid().ToString("N");
            Uploads[id] = ms.ToArray();
            return Results.Json(new { id });
        });

        lz.MapPost("/documents", async (HttpRequest r) =>
        {
            if (Injected("lz:create") is { } f) return f;
            var body = JsonNode.Parse(await new StreamReader(r.Body).ReadToEndAsync())!;
            var uploadId = body["files"]![0]!["id"]!.GetValue<string>();
            if (!Uploads.TryGetValue(uploadId, out var content)) return Results.BadRequest(new { error = "unknown upload" });
            var d = new LzDocument
            {
                Id = Guid.NewGuid().ToString(), Content = content, Description = body["description"]!.GetValue<string>(),
                Actions = body["flowActions"]!.AsArray().Select(a => new LzAction
                {
                    Name = a!["user"]!["name"]!.GetValue<string>(), Email = a["user"]!["email"]!.GetValue<string>(),
                    Identifier = a["user"]!["identifier"]?.GetValue<string>() ?? "", Step = a["step"]!.GetValue<int>()
                }).ToList()
            };
            Documents[d.Id] = d;
            return Results.Json(new[] { new { documentId = d.Id } });
        });

        lz.MapGet("/documents/{id}", (string id) =>
        {
            if (Injected("lz:get") is { } f) return f;
            return Documents.TryGetValue(id, out var d)
                ? Results.Json(new { id = d.Id, status = d.Status, flowActions = d.Actions.Select(a => new { status = a.Status, step = a.Step }).ToList() })
                : Results.NotFound();
        });

        lz.MapPost("/documents/{id}/cancellation", async (string id, HttpRequest r) =>
        {
            if (!Documents.TryGetValue(id, out var d)) return Results.NotFound();
            d.Status = "Canceled";
            d.CancelReason = JsonNode.Parse(await new StreamReader(r.Body).ReadToEndAsync())?["reason"]?.GetValue<string>() ?? "";
            return Results.Ok();
        });

        lz.MapGet("/documents/{id}/content", (string id, string type) =>
        {
            if (!Documents.TryGetValue(id, out var d)) return Results.NotFound();
            return type switch
            {
                "Signatures" => Results.File(d.Content.Concat(Encoding.ASCII.GetBytes("\n%%LACUNA-SIGNED\n")).ToArray(), "application/pdf"),
                "PrinterFriendlyVersion" => Results.File(Encoding.ASCII.GetBytes("%PDF-1.4 lacuna signature report " + id), "application/pdf"),
                _ => Results.BadRequest()
            };
        });
    }

    /// <summary>DocuSign Connect signature: base64 HMAC-SHA256 of the raw body.</summary>
    public static string ConnectSignature(string body, string key = ConnectKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body)));
}
