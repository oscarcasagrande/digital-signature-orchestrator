using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestrator.Application.Processes;
using Orchestrator.Application.Security;
using Xunit;

namespace Orchestrator.UnitTests;

public class CreateProcessValidationTests
{
    public static JsonObject Valid() => JsonNode.Parse("""
    {
      "externalId": "CONTRACT-1",
      "document": { "fileName": "c.pdf", "source": { "type": "URL", "url": "https://example.com/c.pdf" } },
      "signers": [ { "externalId": "s1", "name": "Joao", "document": "12345678909" } ],
      "identityProofing": { "validations": [ "FACE_MATCH", { "type": "LIVENESS", "required": true } ] },
      "signature": { "type": "ADVANCED" },
      "callback": { "url": "https://cliente.exemplo.com/cb" }
    }
    """)!.AsObject();

    [Fact]
    public void Valid_payload_has_no_errors() => CreateProcessValidator.Validate(Valid()).Should().BeEmpty();

    [Fact]
    public void Missing_signers_is_rejected()
    {
        var p = Valid(); p["signers"] = new JsonArray();
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field == "signers");
    }

    [Fact]
    public void Unknown_signature_type_is_rejected()
    {
        var p = Valid(); p["signature"]!["type"] = "MAGIC";
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field == "signature.type");
    }

    [Fact]
    public void Missing_document_is_rejected()
    {
        var p = Valid(); p.Remove("document");
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field == "document");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("providers")]
    public void Vendor_fields_are_rejected(string field)
    {
        var p = Valid(); p[field] = "ANY";
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field == field);
    }

    [Fact]
    public void Unknown_capability_is_rejected()
    {
        var p = Valid(); p["identityProofing"]!["validations"] = new JsonArray("TELEPATHY");
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field.StartsWith("identityProofing"));
    }

    [Fact]
    public void Callback_with_both_url_and_callback_id_is_rejected()
    {
        var p = Valid(); p["callback"]!["callbackId"] = "CB";
        CreateProcessValidator.Validate(p).Should().Contain(e => e.Field == "callback");
    }

    [Theory]
    [InlineData("12345678909", true)]
    [InlineData("11111111111", false)]
    [InlineData("12345678900", false)]
    [InlineData("123", false)]
    [InlineData(null, false)]
    public void Cpf_validation(string? cpf, bool valid) => CreateProcessValidator.IsValidCpf(cpf).Should().Be(valid);

    [Fact]
    public void Hash_is_independent_of_property_order_and_whitespace()
    {
        var a = JsonNode.Parse("""{"b":1,"a":{"y":[1,2],"x":"z"}}""");
        var b = JsonNode.Parse("""{ "a": { "x": "z", "y": [1,2] }, "b": 1 }""");
        CreateProcessValidator.Hash(a).Should().Be(CreateProcessValidator.Hash(b));
    }

    [Fact]
    public void Hash_changes_with_content()
    {
        var a = JsonNode.Parse("""{"a":1}""");
        var b = JsonNode.Parse("""{"a":2}""");
        CreateProcessValidator.Hash(a).Should().NotBe(CreateProcessValidator.Hash(b));
    }

    [Fact]
    public void Masker_hides_all_but_last_two_digits() =>
        SensitiveMasker.MaskDocument("123.456.789-09").Should().Be("*********09");
}
