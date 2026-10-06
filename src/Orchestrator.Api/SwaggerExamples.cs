using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Orchestrator.Api;

/// <summary>Adds request and response examples (the endpoints read raw JSON, so Swagger cannot infer them).</summary>
public sealed class SwaggerExamplesFilter : IOperationFilter
{
    private const string SimpleCreate = """
    {
      "externalId": "CONTRATO-2026-001",
      "document": { "fileName": "contrato.pdf", "source": { "type": "UPLOAD", "uploadId": "upl_0199a1b2c3d4e5f6" } },
      "defaults": { "signatureType": "ADVANCED", "confirmation": ["EMAIL", "SMS"] },
      "signers": [
        { "name": "Maria Souza", "document": "12345678909", "email": "maria@example.com", "phone": "+5511999990000" },
        { "name": "João Lima", "document": "98765432100", "email": "joao@example.com", "phone": "+5511988880000" }
      ],
      "callback": { "url": "https://cliente.exemplo.com/callbacks/assinatura" }
    }
    """;

    private const string SequentialCreate = """
    {
      "externalId": "CONTRATO-2026-002",
      "document": { "fileName": "contrato.pdf", "source": { "type": "URL", "url": "https://exemplo.com/contrato.pdf" } },
      "defaults": { "signatureType": "ADVANCED", "confirmation": ["EMAIL"] },
      "signers": [
        { "name": "Maria Souza", "document": "12345678909", "email": "maria@example.com", "order": 1 },
        { "name": "João Lima", "document": "98765432100", "email": "joao@example.com", "order": 2,
          "signatureType": "QUALIFIED", "confirmation": ["EMAIL", "WHATSAPP"], "phone": "+5511988880000" },
        { "name": "Ana Costa", "document": "11144477735", "order": 2, "confirmation": [] }
      ]
    }
    """;

    private const string LegacyCreate = """
    {
      "externalId": "CONTRATO-2026-003",
      "document": { "fileName": "contrato.pdf", "source": { "type": "URL", "url": "https://exemplo.com/contrato.pdf" } },
      "signers": [ { "externalId": "cliente-1", "name": "Maria Souza", "document": "12345678909" } ],
      "signature": { "type": "ADVANCED" }
    }
    """;

    private const string Progress = """{ "completedSteps": 3, "totalSteps": 7, "currentStep": { "order": 4, "kind": "CONFIRMATION", "signerId": "sgn_0199a1", "channel": "EMAIL", "label": "Confirmação por e-mail - João Lima", "status": "IN_PROGRESS" } }""";

    private static readonly Dictionary<string, Action<OpenApiOperation>> Examples = new()
    {
        ["POST /v1/signature-processes"] = op =>
        {
            op.Summary = "Cria um processo de assinatura";
            op.Description = "Signatários aceitam name, document (CPF), email, phone, signatureType, order e confirmation (canais EMAIL, SMS, WHATSAPP). "
                + "`defaults.signatureType` e `defaults.confirmation` são herdados por quem não informa os seus; `confirmation: []` desliga a herança. "
                + "Sem `order` todos assinam em paralelo; com `order`, valores iguais assinam juntos e não pode haver lacunas. "
                + "Erros de validação apontam `signers[i].campo`. O payload anterior (signature.type, externalId do signatário) continua válido.";
            op.Parameters.Add(new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true, Schema = new OpenApiSchema { Type = "string", MaxLength = 128 } });
            Body(op, ("simples", "Lista de nomes e contatos com defaults", SimpleCreate), ("sequencial", "Assinatura sequencial com exceções por pessoa", SequentialCreate),
                ("legado", "Payload original (compatível)", LegacyCreate));
            Response(op, "202", "Processo criado", """{ "processId": "sig_0199a1b2c3d4", "externalId": "CONTRATO-2026-001", "businessStatus": "CREATED", "operationalStatus": "READY", "createdAt": "2026-10-06T12:00:00Z" }""");
            Response(op, "400", "Erro de validação por signatário e campo", """
                { "title": "Validation failed", "status": 400, "errors": {
                  "signers[1].phone": ["Required when the confirmation channel SMS or WHATSAPP is used"],
                  "signers[2].order": ["Order has a gap: no signer has order 2"] } }
                """);
        },
        ["GET /v1/signature-processes"] = op =>
        {
            op.Summary = "Lista processos, com o progresso de cada um";
            Response(op, "200", "Página de processos", $$"""
                { "items": [ { "processId": "sig_0199a1b2c3d4", "externalId": "CONTRATO-2026-001", "documentFileName": "contrato.pdf", "provider": "SIMULATED",
                  "signersSigned": 1, "signersTotal": 2, "businessStatus": "SIGNATURE_IN_PROGRESS", "operationalStatus": "READY",
                  "createdAt": "2026-10-06T12:00:00Z", "updatedAt": "2026-10-06T12:03:00Z", "sla": "OK", "progress": {{Progress}} } ],
                  "page": 1, "pageSize": 50, "total": 1 }
                """);
        },
        ["GET /v1/signature-processes/{id}"] = op =>
        {
            op.Summary = "Detalhe do processo: signatários, confirmações e etapas";
            Response(op, "200", "Processo", $$"""
                { "processId": "sig_0199a1b2c3d4", "businessStatus": "SIGNATURE_IN_PROGRESS", "operationalStatus": "READY", "signatureType": "ADVANCED",
                  "signers": [ { "id": "sgn_0199a1", "externalId": "signer-1", "name": "Maria Souza", "document": "*********09", "email": "m***@example.com",
                    "phone": "*********00", "signatureType": "ADVANCED", "order": null, "signed": true, "signedAt": "2026-10-06T12:02:00Z",
                    "confirmations": [ { "channel": "EMAIL", "status": "CONFIRMED", "sendCount": 1, "attemptsRemaining": null, "confirmedAt": "2026-10-06T12:01:00Z" } ] } ],
                  "progress": { "completedSteps": 3, "totalSteps": 7, "currentStep": {{CurrentStep}}, "steps": [
                    { "order": 1, "kind": "DOCUMENT_RECEIVED", "signerId": null, "channel": null, "label": "Documento recebido", "status": "COMPLETED" },
                    { "order": 2, "kind": "CONFIRMATION", "signerId": "sgn_0199a1", "channel": "EMAIL", "label": "Confirmação por e-mail - Maria Souza", "status": "COMPLETED" },
                    { "order": 3, "kind": "SIGNATURE", "signerId": "sgn_0199a1", "channel": null, "label": "Assinatura - Maria Souza", "status": "COMPLETED" },
                    { "order": 4, "kind": "CONFIRMATION", "signerId": "sgn_0199a2", "channel": "EMAIL", "label": "Confirmação por e-mail - João Lima", "status": "IN_PROGRESS" } ] } }
                """);
        },
        ["POST /v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/confirm"] = op =>
        {
            Body(op, ("codigo", "Código digitado pelo signatário", """{ "code": "123456" }"""));
            Response(op, "200", "Canal confirmado", """{ "channel": "EMAIL", "status": "CONFIRMED" }""");
            Response(op, "422", "Código incorreto", """{ "title": "Code does not match", "status": 422, "attemptsRemaining": 4 }""");
            Response(op, "410", "Código expirado (solicite outro em /resend)", """{ "title": "Code expired", "status": 410 }""");
            Response(op, "423", "Código bloqueado por tentativas (solicite outro em /resend)", """{ "title": "Code locked", "status": 423 }""");
        },
        ["POST /v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/resend"] = op =>
        {
            Response(op, "202", "Novo código em envio", """{ "channel": "EMAIL", "operationId": "op_0199a1b2", "status": "SENDING" }""");
            Response(op, "429", "Intervalo mínimo ou limite de envios (cabeçalho Retry-After quando aplicável)", """{ "title": "Too many requests", "status": 429, "detail": "Wait before requesting a new code" }""");
        },
        ["POST /v1/document-uploads"] = op =>
        {
            op.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = { ["multipart/form-data"] = new OpenApiMediaType { Schema = new OpenApiSchema
                {
                    Type = "object", Required = new HashSet<string> { "file" },
                    Properties = { ["file"] = new OpenApiSchema { Type = "string", Format = "binary", Description = "Documento a ser assinado (PDF)" } }
                } } }
            };
            Response(op, "201", "Upload guardado; use uploadId em document.source", """{ "uploadId": "upl_0199a1b2c3d4e5f6", "fileName": "contrato.pdf", "contentType": "application/pdf", "size": 48213, "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", "expiresAt": "2026-10-07T12:00:00Z" }""");
        }
    };

    private const string CurrentStep = """{ "order": 4, "kind": "CONFIRMATION", "signerId": "sgn_0199a2", "channel": "EMAIL", "label": "Confirmação por e-mail - João Lima", "status": "IN_PROGRESS" }""";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var key = $"{context.ApiDescription.HttpMethod} /{context.ApiDescription.RelativePath?.Split('?')[0].TrimEnd('/')}";
        if (Examples.TryGetValue(key, out var apply)) apply(operation);
    }

    private static void Body(OpenApiOperation op, params (string Name, string Summary, string Json)[] examples)
    {
        var media = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "object" } };
        foreach (var (name, summary, json) in examples) media.Examples[name] = new OpenApiExample { Summary = summary, Value = ToAny(JsonNode.Parse(json)) };
        op.RequestBody = new OpenApiRequestBody { Required = true, Content = { ["application/json"] = media } };
    }

    private static void Response(OpenApiOperation op, string status, string description, string json)
    {
        var media = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "object" }, Example = ToAny(JsonNode.Parse(json)) };
        op.Responses[status] = new OpenApiResponse { Description = description, Content = { ["application/json"] = media } };
    }

    private static IOpenApiAny ToAny(JsonNode? n) => n switch
    {
        null => new OpenApiNull(),
        JsonObject o => Fill(new OpenApiObject(), o),
        JsonArray a => Fill(new OpenApiArray(), a),
        JsonValue v when v.TryGetValue<string>(out var s) => new OpenApiString(s),
        JsonValue v when v.TryGetValue<bool>(out var b) => new OpenApiBoolean(b),
        JsonValue v when v.TryGetValue<long>(out var l) => new OpenApiLong(l),
        JsonValue v when v.TryGetValue<double>(out var d) => new OpenApiDouble(d),
        _ => new OpenApiString(n.ToJsonString())
    };

    private static OpenApiObject Fill(OpenApiObject target, JsonObject o)
    {
        foreach (var kv in o) target[kv.Key] = ToAny(kv.Value);
        return target;
    }

    private static OpenApiArray Fill(OpenApiArray target, JsonArray a)
    {
        foreach (var item in a) target.Add(ToAny(item));
        return target;
    }
}
