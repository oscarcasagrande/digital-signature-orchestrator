# Tasks: Segurança e Observabilidade

## Phase 1: Setup
- [X] T001 Adicionar pacotes JwtBearer, OpenTelemetry e Serilog.Sinks.OpenTelemetry em src/Orchestrator.Api e src/Orchestrator.Worker (Telemetry na Application sem pacote extra, usa System.Diagnostics)

## Phase 2: Foundational
- [X] T002 `Telemetry` (ActivitySource, Meter e instrumentos) em src/Orchestrator.Application/Telemetry.cs
- [X] T003 Migration `AddClientSegregation`: `client_id` em `signature_process`; idempotência por cliente via chave com hash (client + key), sem coluna extra

## Phase 3: US1 e US2 - Autenticação, RBAC e segregação (P1)
- [X] T004 [US1] Testes de integração `AuthApiTests` (401, 403, matriz de papéis, auditoria do ator, segregação, idempotência por cliente) em tests/Orchestrator.IntegrationTests/AuthApiTests.cs
- [X] T005 [US1] `AuthOptions`, JwtBearer, políticas, mapeamento de roles e fallback em src/Orchestrator.Api/Security.cs e Program.cs
- [X] T006 [US1] `CallerContext` e `ActorMiddleware` com identidade do token; 401/403 em problem+json e métrica de negados
- [X] T007 [US2] Gravar `client_id` na criação, filtrar consultas e operações por cliente (404 para alheio)
- [X] T008 [US1] Aplicar políticas nos endpoints (leitura, operar, criar, admin)
- [X] T009 [US2] Rate limit na criação de processos

## Phase 4: US4 e US5 - Observabilidade (P1)
- [X] T010 [US4] Testes `TelemetryTests` (trace único com tags, instrumentos) em tests/Orchestrator.IntegrationTests/TelemetryTests.cs
- [X] T011 [US4] Span `operation.execute`, propagação de `traceparent` por outbox/AMQP/consumidor
- [X] T012 [US5] Instrumentos: processos criados/concluídos/falhos e tempo, latência e erros do provider, retries, DLQ, callbacks, reconciliação, proofing
- [X] T013 [US4] Registro OpenTelemetry (traces, métricas) e logs Serilog com TraceId/CorrelationId em Api e Worker

## Phase 5: Infra, portal e verificação
- [X] T014 Keycloak (realm de demonstração) e OpenTelemetry Collector no docker-compose; variáveis `Auth__*` e `OTEL_*`
- [X] T015 [US3] Portal: AuthContext, Login, Bearer, papéis, 401 e testes Vitest
- [X] T016 Smoke test com tokens do Keycloak, segregação e métricas no coletor
- [X] T017 README, DECISIONS, PROGRESS; mapa de cobertura dos 16 critérios do PRD seção 49 e relatório final
