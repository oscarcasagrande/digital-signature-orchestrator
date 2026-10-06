using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Orchestrator.Application.Providers;
using Orchestrator.Infrastructure.Providers.Real;
using Xunit;

namespace Orchestrator.UnitTests;

public class DotEnvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotenv-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _keys = [];

    public DotEnvTests() => Directory.CreateDirectory(Path.Combine(_root, "a", "b"));

    private string Key(string suffix)
    {
        var k = "TESTENV_" + Guid.NewGuid().ToString("N")[..8] + "_" + suffix;
        _keys.Add(k);
        return k;
    }

    public void Dispose()
    {
        foreach (var k in _keys) Environment.SetEnvironmentVariable(k, null);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Loads_variables_from_the_nearest_env_file_up_the_tree_handling_comments_quotes_and_export()
    {
        var (plain, quoted, single, exported, inline, empty) = (Key("P"), Key("Q"), Key("S"), Key("E"), Key("I"), Key("Z"));
        File.WriteAllText(Path.Combine(_root, ".env"),
            $"# comment\n\n{plain}=value one\n{quoted}=\"has # hash and =\"\n{single}='single'\nexport {exported}=exp\n{inline}=abc # trailing\n{empty}=\nnot a pair\n");
        var loaded = DotEnv.Load(Path.Combine(_root, "a", "b"));
        loaded.Should().Be(6);
        Environment.GetEnvironmentVariable(plain).Should().Be("value one");
        Environment.GetEnvironmentVariable(quoted).Should().Be("has # hash and =");
        Environment.GetEnvironmentVariable(single).Should().Be("single");
        Environment.GetEnvironmentVariable(exported).Should().Be("exp");
        Environment.GetEnvironmentVariable(inline).Should().Be("abc");
        Environment.GetEnvironmentVariable(empty).Should().BeNullOrEmpty(); // an empty value is "unset" on Windows
    }

    [Fact]
    public void Existing_environment_variables_win_over_the_file()
    {
        var k = Key("W");
        Environment.SetEnvironmentVariable(k, "from-environment");
        File.WriteAllText(Path.Combine(_root, ".env"), $"{k}=from-file\n");
        DotEnv.Load(_root).Should().Be(0);
        Environment.GetEnvironmentVariable(k).Should().Be("from-environment");
    }

    [Fact]
    public void A_missing_file_is_not_an_error() => DotEnv.Load(Path.Combine(_root, "a")).Should().BeGreaterThanOrEqualTo(0);

    [Fact]
    public void The_env_file_is_git_ignored_and_the_example_documents_every_variable()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DigitalSignature.sln"))) repo = repo.Parent;
        repo.Should().NotBeNull();
        File.ReadAllLines(Path.Combine(repo!.FullName, ".gitignore")).Select(l => l.Trim()).Should().Contain(".env");
        var example = File.ReadAllText(Path.Combine(repo.FullName, ".env.example"));
        foreach (var v in new[]
                 {
                     "Provider__Default", "DOCUSIGN_INTEGRATION_KEY", "DOCUSIGN_USER_ID", "DOCUSIGN_ACCOUNT_ID", "DOCUSIGN_PRIVATE_KEY", "DOCUSIGN_PRIVATE_KEY_FILE",
                     "DOCUSIGN_BASE_URI", "DOCUSIGN_AUTH_SERVER", "DOCUSIGN_CONNECT_HMAC_KEY", "LACUNA_API_KEY", "LACUNA_BASE_URI", "LACUNA_WEBHOOK_SECRET"
                 })
            example.Should().Contain(v);
        // the example never carries a real value
        foreach (var line in example.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('=')))
            line[(line.IndexOf('=') + 1)..].Should().BeOneOf("", "Simulated", "https://demo.docusign.net/restapi", "account-d.docusign.com",
                "https://signer-lac.azurewebsites.net", "./docusign-private.pem", "\"-----BEGIN RSA PRIVATE KEY-----\\n...\\n-----END RSA PRIVATE KEY-----\"");
    }
}

public class ProviderOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Provider_names_are_normalised_and_unknown_ones_are_refused()
    {
        ProviderCodes.Normalize(null).Should().Be("SIMULATED");
        ProviderCodes.Normalize("").Should().Be("SIMULATED");
        ProviderCodes.Normalize("docusign").Should().Be("DOCUSIGN");
        ProviderCodes.Normalize(" Lacuna ").Should().Be("LACUNA");
        Assert.Throws<InvalidOperationException>(() => ProviderCodes.Normalize("whatever"));
    }

    [Fact]
    public void DocuSign_options_default_to_the_demo_environment_and_list_missing_variables_by_name()
    {
        var o = DocuSignOptions.From(Config(new() { ["DOCUSIGN_INTEGRATION_KEY"] = "ik" }));
        o.BaseUri.Should().Be("https://demo.docusign.net/restapi");
        o.AuthServer.Should().Be("account-d.docusign.com");
        o.Missing().Should().BeEquivalentTo("DOCUSIGN_USER_ID", "DOCUSIGN_ACCOUNT_ID", "DOCUSIGN_PRIVATE_KEY or DOCUSIGN_PRIVATE_KEY_FILE");
        o.Missing().Should().NotContain(m => m.Contains("ik"));
    }

    [Fact]
    public void Private_key_accepts_escaped_newlines_and_a_file()
    {
        var pem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
        DocuSignOptions.From(Config(new() { ["DOCUSIGN_PRIVATE_KEY"] = pem.Replace("\n", "\\n") })).PrivateKeyPem.Should().Be(pem);
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, pem);
            DocuSignOptions.From(Config(new() { ["DOCUSIGN_PRIVATE_KEY_FILE"] = file, ["DOCUSIGN_PRIVATE_KEY"] = "ignored" })).PrivateKeyPem.Should().Be(pem);
        }
        finally { File.Delete(file); }
        DocuSignOptions.From(Config(new() { ["DOCUSIGN_PRIVATE_KEY"] = "   " })).PrivateKeyPem.Should().BeNull();
    }

    [Fact]
    public void Lacuna_options_default_to_the_demo_environment()
    {
        var o = LacunaOptions.From(Config(new()));
        o.BaseUri.Should().Be("https://signer-lac.azurewebsites.net");
        o.Missing().Should().Equal("LACUNA_API_KEY");
        LacunaOptions.From(Config(new() { ["LACUNA_API_KEY"] = "app|key", ["LACUNA_BASE_URI"] = "https://x.example/" })).BaseUri.Should().Be("https://x.example");
    }

    [Fact]
    public void The_jwt_assertion_is_a_valid_rs256_token_with_the_integration_claims()
    {
        using var rsa = RSA.Create(2048);
        var options = new DocuSignOptions("ik-1", "user-1", "acc-1", rsa.ExportPkcs8PrivateKeyPem(), DocuSignOptions.DefaultBaseUri, DocuSignOptions.DefaultAuthServer, null);
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        var jwt = DocuSignTokenProvider.BuildAssertion(options, "account-d.docusign.com", now).Split('.');
        static byte[] Dec(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
        rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Dec(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
        JsonNode.Parse(Dec(jwt[0]))!["alg"]!.GetValue<string>().Should().Be("RS256");
        var claims = JsonNode.Parse(Dec(jwt[1]))!;
        claims["iss"]!.GetValue<string>().Should().Be("ik-1");
        claims["sub"]!.GetValue<string>().Should().Be("user-1");
        claims["aud"]!.GetValue<string>().Should().Be("account-d.docusign.com");
        claims["scope"]!.GetValue<string>().Should().Be("signature impersonation");
        (claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>()).Should().Be(3600);
    }

    [Fact]
    public void An_invalid_private_key_is_reported_without_echoing_it()
    {
        var options = new DocuSignOptions("ik", "u", "a", "not a pem SECRET-MATERIAL", DocuSignOptions.DefaultBaseUri, DocuSignOptions.DefaultAuthServer, null);
        var ex = Assert.Throws<Orchestrator.Application.Providers.ProviderException>(() => DocuSignTokenProvider.BuildAssertion(options, "x", DateTime.UtcNow));
        ex.Transient.Should().BeFalse();
        ex.Message.Should().NotContain("SECRET-MATERIAL");
    }
}

public class WebhookHandlerTests
{
    private static string Sign(string body, string key) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body)));
    private static DocuSignWebhookHandler Ds(string? key = "k") => new(new DocuSignOptions(null, null, null, null, "", "", key));
    private const string Body = """{"event":"envelope-completed","data":{"envelopeId":"env-1"}}""";

    [Fact]
    public void DocuSign_accepts_a_valid_hmac_on_any_signature_header_and_extracts_the_envelope()
    {
        var r = Ds().Verify(new Dictionary<string, string> { ["X-DocuSign-Signature-2"] = Sign(Body, "k"), ["X-DocuSign-Signature-1"] = Sign(Body, "old") }, Body);
        (r.Authentic, r.ProviderProcessId, r.EventType).Should().Be((true, "env-1", "envelope-completed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("%%%not base64")]
    public void DocuSign_refuses_garbage_signatures(string signature) =>
        Ds().Verify(new Dictionary<string, string> { ["X-DocuSign-Signature-1"] = signature }, Body).Authentic.Should().BeFalse();

    [Fact]
    public void DocuSign_refuses_tampered_bodies_missing_headers_and_missing_keys()
    {
        var good = Sign(Body, "k");
        Ds().Verify(new Dictionary<string, string> { ["X-DocuSign-Signature-1"] = good }, Body.Replace("env-1", "env-2")).Authentic.Should().BeFalse();
        Ds().Verify(new Dictionary<string, string>(), Body).Authentic.Should().BeFalse();
        Ds(key: null).Verify(new Dictionary<string, string> { ["X-DocuSign-Signature-1"] = Sign(Body, "") }, Body).Authentic.Should().BeFalse();
    }

    [Fact]
    public void Lacuna_compares_the_shared_secret_and_finds_the_document_id_in_known_shapes()
    {
        var h = new LacunaWebhookHandler(new LacunaOptions("k", "u", "s3cret"));
        h.Verify(new Dictionary<string, string> { ["x-webhook-secret"] = "s3cret" }, """{"documentId":"d1","type":"DocumentConcluded"}""")
            .Should().Be(new ProviderWebhookResultProxy(true, "d1", "DocumentConcluded").ToResult());
        h.Verify(new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" }, """{"data":{"documentId":"d2"}}""").ProviderProcessId.Should().Be("d2");
        h.Verify(new Dictionary<string, string> { ["X-Webhook-Secret"] = "s3cret" }, """{"id":"d3"}""").ProviderProcessId.Should().Be("d3");
        h.Verify(new Dictionary<string, string> { ["X-Webhook-Secret"] = "nope" }, "{}").Authentic.Should().BeFalse();
        h.Verify(new Dictionary<string, string>(), "{}").Authentic.Should().BeFalse();
        new LacunaWebhookHandler(new LacunaOptions("k", "u", null)).Verify(new Dictionary<string, string> { ["X-Webhook-Secret"] = "" }, "{}").Authentic.Should().BeFalse();
    }

    private sealed record ProviderWebhookResultProxy(bool A, string? P, string? E)
    {
        public Orchestrator.Application.Providers.ProviderWebhookResult ToResult() => new(A, P, E);
    }
}
