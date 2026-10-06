using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestrator.Application.Processes;
using Xunit;

namespace Orchestrator.UnitTests;

public class SignerValidationTests
{
    private const string Cpf = "12345678909";

    private static JsonObject Payload(string signers, string? defaults = null, string? signature = null) => JsonNode.Parse($$"""
    {
      "externalId": "C-1",
      "document": { "fileName": "c.pdf", "source": { "type": "URL", "url": "https://example.com/c.pdf" } },
      "signers": {{signers}}
      {{(defaults is null ? "" : ", \"defaults\": " + defaults)}}
      {{(signature is null ? "" : ", \"signature\": " + signature)}}
    }
    """)!.AsObject();

    private static List<FieldError> Errors(JsonObject o) => CreateProcessValidator.Validate(o);

    [Fact]
    public void Simple_case_is_names_and_contacts_with_defaults()
    {
        var o = Payload($$"""[{"name":"Ana","document":"{{Cpf}}","email":"ana@x.com"},{"name":"Bia","document":"{{Cpf}}","email":"bia@x.com"}]""",
            defaults: """{"signatureType":"ADVANCED","confirmation":["EMAIL"]}""");
        Errors(o).Should().BeEmpty();
        var (signers, _) = SignerPlan.Resolve(o);
        signers.Should().OnlyContain(s => s.SignatureType == "ADVANCED" && s.Channels.SequenceEqual(new[] { "EMAIL" }) && s.Order == null);
        signers.Select(s => s.ExternalId).Should().Equal("signer-1", "signer-2");
    }

    [Fact]
    public void Signer_values_override_defaults_and_empty_list_disables_confirmation()
    {
        var o = Payload($$"""
            [{"name":"Ana","document":"{{Cpf}}","email":"a@x.com","phone":"+5511999990000","signatureType":"QUALIFIED","confirmation":["SMS","EMAIL"]},
             {"name":"Bia","document":"{{Cpf}}","confirmation":[]}]
            """, defaults: """{"signatureType":"SIMPLE","confirmation":["EMAIL"]}""");
        Errors(o).Should().BeEmpty();
        var (s, _) = SignerPlan.Resolve(o);
        s[0].SignatureType.Should().Be("QUALIFIED");
        s[0].Channels.Should().Equal("SMS", "EMAIL");
        s[1].SignatureType.Should().Be("SIMPLE");
        s[1].Channels.Should().BeEmpty();
    }

    [Fact]
    public void Legacy_payload_still_valid_and_process_type_is_inherited()
    {
        var o = Payload($$"""[{"externalId":"s1","name":"Ana","document":"{{Cpf}}"}]""", signature: """{"type":"ADVANCED"}""");
        Errors(o).Should().BeEmpty();
        SignerPlan.Resolve(o).Signers.Single().SignatureType.Should().Be("ADVANCED");
    }

    [Fact]
    public void Missing_type_everywhere_points_at_the_signer()
    {
        var o = Payload($$"""[{"name":"Ana","document":"{{Cpf}}"},{"name":"Bia","document":"{{Cpf}}","signatureType":"SIMPLE"}]""");
        Errors(o).Should().ContainSingle(e => e.Field == "signers[0].signatureType");
    }

    [Theory]
    [InlineData("""["SMS"]""", "signers[0].phone")]
    [InlineData("""["WHATSAPP"]""", "signers[0].phone")]
    [InlineData("""["EMAIL"]""", "signers[0].email")]
    public void Channel_requires_its_contact(string channels, string field)
    {
        var o = Payload($$"""[{"name":"Ana","document":"{{Cpf}}","signatureType":"SIMPLE","confirmation":{{channels}}}]""");
        Errors(o).Should().Contain(e => e.Field == field);
    }

    [Fact]
    public void Inherited_channel_without_contact_is_reported_on_the_signer()
    {
        var o = Payload($$"""[{"name":"Ana","document":"{{Cpf}}","email":"a@x.com"},{"name":"Bia","document":"{{Cpf}}"}]""",
            defaults: """{"signatureType":"SIMPLE","confirmation":["EMAIL"]}""");
        Errors(o).Should().ContainSingle().Which.Field.Should().Be("signers[1].email");
    }

    [Theory]
    [InlineData("""{"name":"A","document":"11111111111","signatureType":"SIMPLE"}""", "signers[0].document")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"SIMPLE","email":"nope"}""", "signers[0].email")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"SIMPLE","phone":"123"}""", "signers[0].phone")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"MAGIC"}""", "signers[0].signatureType")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"SIMPLE","confirmation":["FAX"]}""", "signers[0].confirmation[0]")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"SIMPLE","confirmation":"EMAIL"}""", "signers[0].confirmation")]
    [InlineData("""{"name":"A","document":"12345678909","signatureType":"SIMPLE","order":0}""", "signers[0].order")]
    public void Invalid_fields_point_at_the_signer_and_field(string signer, string field)
    {
        Errors(Payload("[" + signer + "]")).Should().Contain(e => e.Field == field);
    }

    [Fact]
    public void Errors_identify_the_right_signer_among_many()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE"},{"name":"B","document":"123","signatureType":"SIMPLE"},{"document":"{{Cpf}}","signatureType":"SIMPLE"}]""");
        var f = Errors(o).Select(e => e.Field).ToList();
        f.Should().BeEquivalentTo("signers[1].document", "signers[2].name");
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,1,2")]
    [InlineData("2,1,1")]
    public void Order_without_gaps_is_accepted(string orders)
    {
        var signers = string.Join(",", orders.Split(',').Select(o => $$"""{"name":"S","document":"{{Cpf}}","signatureType":"SIMPLE","order":{{o}}}"""));
        Errors(Payload("[" + signers + "]")).Should().BeEmpty();
    }

    [Fact]
    public void Order_gap_points_at_the_first_signer_beyond_the_gap()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE","order":1},{"name":"B","document":"{{Cpf}}","signatureType":"SIMPLE","order":3}]""");
        var e = Errors(o).Should().ContainSingle().Subject;
        e.Field.Should().Be("signers[1].order");
        e.Message.Should().Contain("gap");
    }

    [Fact]
    public void Order_starting_above_one_is_a_gap()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE","order":2}]""");
        Errors(o).Should().ContainSingle(e => e.Field == "signers[0].order");
    }

    [Fact]
    public void Order_must_be_informed_by_everyone_or_nobody()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE","order":1},{"name":"B","document":"{{Cpf}}","signatureType":"SIMPLE"}]""");
        Errors(o).Should().ContainSingle(e => e.Field == "signers[1].order");
    }

    [Fact]
    public void Defaults_are_validated()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE"}]""", defaults: """{"signatureType":"X","confirmation":["EMAIL","EMAIL","PIGEON"]}""");
        Errors(o).Select(e => e.Field).Should().Contain(["defaults.signatureType", "defaults.confirmation[1]", "defaults.confirmation[2]"]);
    }

    [Theory]
    [InlineData("+55 (11) 99999-0000", "+5511999990000")]
    [InlineData("11999990000", "11999990000")]
    [InlineData("abc", null)]
    [InlineData("12345", null)]
    public void Phone_is_normalized(string raw, string? expected) => SignerPlan.NormalizePhone(raw).Should().Be(expected);

    [Fact]
    public void Upload_source_requires_the_upload_id()
    {
        var o = Payload($$"""[{"name":"A","document":"{{Cpf}}","signatureType":"SIMPLE"}]""");
        o["document"]!["source"] = JsonNode.Parse("""{"type":"UPLOAD"}""");
        Errors(o).Should().Contain(e => e.Field == "document.source.uploadId");
        o["document"]!["source"] = JsonNode.Parse("""{"type":"UPLOAD","uploadId":"upl_1"}""");
        Errors(o).Should().BeEmpty();
    }
}
