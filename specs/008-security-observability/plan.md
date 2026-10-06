# Implementation Plan: Segurança e Observabilidade

**Branch**: `008-security-observability` | **Spec**: [spec.md](spec.md)

## Technical Context

- .NET 8, ASP.NET Core JwtBearer (`Microsoft.AspNetCore.Authentication.JwtBearer` 8.x), políticas de autorização por papel.
- OpenTelemetry .NET: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`; logs via Serilog `Serilog.Sinks.OpenTelemetry`.
- Keycloak 25 (`quay.io/keycloak/keycloak:25.0`, `start-dev --import-realm`), OpenTelemetry Collector contrib 0.104.
- Portal: React com contexto de autenticação (login por formulário), fetch com Bearer.
- Sem novos serviços pagos.

## Constitution Check

Secure by design com testes (atende); auditoria com identidade (atende); OpenTelemetry (atende); segredo só de demonstração em arquivos de dev (documentado); idempotência e outbox preservados (propagação de trace no payload do outbox, sem coluna nova).

## Design

### Segurança
- `AuthOptions { Enabled, Authority, MetadataAddress, Audience, ValidIssuers, SigningKey (somente testes), RequireHttpsMetadata }`.
- `Auth:Enabled=false`: nenhum esquema; `ActorMiddleware` como hoje.
- `Auth:Enabled=true`: `AddAuthentication().AddJwtBearer` (autoridade do Keycloak ou chave simétrica em testes), mapeamento de `realm_access.roles` para `ClaimTypes.Role` em `OnTokenValidated`. `FallbackPolicy = RequireAuthenticatedUser`; `/health` e swagger com `AllowAnonymous`. Políticas: `Read` (viewer, operator, client, admin), `Operate` (operator, admin, client), `Create` (client, admin), `Admin`.
- Matriz: GET leituras → Read; cancel, reconcile, retry, reprocess, download-link, provider, dead-letters resolve → Operate (client só nos próprios); criar processo → Create; callbacks de registro e proofing → admin/client (client segregado); `viewer` não opera.
- `CallerContext` (escopo de requisição): `UserId`, `Roles`, `ClientId` (somente quando papel `client` sem `operator`/`admin`). `ActorMiddleware` preenche `ActorContext` e `CallerContext` do token.
- Segregação: coluna `client_id` (varchar 100, nula) em `signature_process` e em `idempotency_record`; chave de idempotência por (client, key). `ProcessQueries` e demais consultas filtram por `ClientId` quando presente; resource de outro cliente → 404.
- Rate limit: `AddRateLimiter` com janela fixa por `client_id`/IP na criação (`RateLimit:CreatePerMinute`, 600).
- Falhas 401/403 incrementam `orchestrator.security.denied` e logam.

### Observabilidade
- `Telemetry` estático na Application: `ActivitySource("Orchestrator")`, `Meter("Orchestrator")` com os instrumentos do spec.
- Contagem de criados/concluídos/falhos e tempo de conclusão: detecção das mudanças de `BusinessStatus` em `SaveChangesAsync` do DbContext, após sucesso.
- Span `operation.execute` no `WorkflowEngine.ExecuteAsync` com tags de correlação; `provider.id` do `ProviderProcess` ou do adapter.
- Propagação: `EventRecorder.EnqueueOperation` grava `traceparent` (de `Activity.Current`) no payload; o publisher copia para o header AMQP; o consumidor inicia o span com contexto pai extraído.
- Métricas de provider via decorator `InstrumentedProviderAdapter`; callbacks, reconciliação, proofing e retries nos pontos de decisão existentes; gauge de DLQ pendente via `IServiceScopeFactory` com cache de 5 s.
- Logs: Serilog com `TraceId`/`SpanId` (Enrich) e propriedade `CorrelationId`; sink OTLP quando `OTEL_EXPORTER_OTLP_ENDPOINT` existe.
- Compose: serviços `keycloak` e `otel-collector`; `deploy/keycloak/realm-orchestrator.json`; `deploy/otel/collector.yaml`.

### Portal
- `AuthContext` (token, usuário, papéis em sessionStorage), página `Login`, `api/client` envia `Authorization: Bearer`; 401 → logout; `OperatorBar` mostra usuário e sair; ações manuais desabilitadas sem papel `operator`/`admin`.

## Testes
- Integração: fábrica com `Auth:Enabled=true` e chave simétrica de teste (`AuthApiTests`: 401/403/200, matriz de papéis, auditoria do ator, segregação cruzada, idempotência por cliente, rate limit); `TelemetryTests` (ActivityListener: trace único, tags; MeterListener: instrumentos).
- Unitários: mapeamento de papéis e `CallerContext`.
- Portal: login, papéis, 401.
- Smoke: tokens reais do Keycloak (client A, client B, operator, viewer), coletor `/metrics`.
