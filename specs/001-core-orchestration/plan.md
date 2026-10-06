# Implementation Plan: Núcleo de Orquestração de Assinaturas

**Branch**: `001-core-orchestration` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

API .NET 8 recebe processos de assinatura (idempotentes), grava processo + operação inicial + evento de journal + outbox numa única transação PostgreSQL. Um Worker publica a outbox no RabbitMQ (fila `signature-provider`) e consome as mensagens com inbox, executando operações encadeadas (download/armazenamento simulados, criação/envio/consulta no provider simulado, download/armazenamento do assinado) até `COMPLETED`. Estado de negócio e operacional são máquinas independentes no domínio; todo avanço é journalizado.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: ASP.NET Core, EF Core 8 + Npgsql (JSONB), RabbitMQ.Client 6.x, Serilog (console), Swashbuckle/OpenAPI
**Storage**: PostgreSQL 16 (JSONB para payload, provider metadata, journal metadata)
**Testing**: xUnit, FluentAssertions, Testcontainers (PostgreSQL, RabbitMQ) para integração; unit tests para domínio
**Target Platform**: Linux containers (Docker Compose); desenvolvimento em Windows
**Project Type**: web-service + worker
**Performance Goals**: consultas < 1 s local; fluxo completo < 30 s
**Constraints**: at-least-once + idempotência; sem exactly-once; mensagens só com referências
**Scale/Scope**: MVP; API e Worker stateless, escaláveis horizontalmente (publisher e consumers usam `FOR UPDATE SKIP LOCKED`)

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Contrato só com capabilities; `IProviderAdapter` isola o fornecedor; rejeição de campo `provider(s)` no payload. PASS |
| II System of record | Documento só referenciado nesta spec; artefatos/hash na spec 002 (Assumptions). PASS (parcial por escopo) |
| III Idempotência | Idempotency-Key + hash do payload; inbox; adapter checa referência externa. PASS |
| IV Outbox | Estado+outbox na mesma transação; publisher separado; mensagens só com ids; fila por domínio. PASS |
| V Máquinas/Operações | Duas máquinas independentes; operações individuais. Retry/DLQ em 003; circuit breaker/bulkhead Fase 2. PASS (escopo) |
| VI Auditoria | Journal append-only (trigger bloqueia UPDATE/DELETE), correlationId/causationId. OTel na spec 008. PASS (escopo) |
| VII Segurança/testes | Máscara de CPF em logs; testes de fluxos críticos; OIDC na spec 008. PASS (escopo) |

Sem violações; sem entradas em Complexity Tracking. Re-check pós-design: PASS.

## Project Structure

### Documentation

```text
specs/001-core-orchestration/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/openapi.yaml, contracts/sample-request.json
└── tasks.md
```

### Source Code

```text
DigitalSignature.sln
src/
├── Orchestrator.Domain/          # entidades, enums, máquinas de estado, erros de domínio
├── Orchestrator.Application/     # casos de uso, IProviderAdapter, workflow, portas
├── Orchestrator.Infrastructure/  # EF Core, migrations, outbox/inbox, RabbitMQ, FakeProviderAdapter
├── Orchestrator.Api/             # endpoints /v1, middleware correlationId, problem+json
└── Orchestrator.Worker/          # OutboxPublisher, OperationConsumer
tests/
├── Orchestrator.UnitTests/       # máquinas de estado, validação, hash de idempotência, fake provider
└── Orchestrator.IntegrationTests/# API + Postgres + RabbitMQ via Testcontainers
deploy/ (Dockerfiles)  docker-compose.yml  scripts/smoke-test.sh
```

**Structure Decision**: Clean Architecture com 5 projetos de produção; justificado pela constitution (adapters desacoplados do domínio) e escala independente de API/Worker.
