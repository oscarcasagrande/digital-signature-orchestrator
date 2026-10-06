# Implementation Plan: Resiliência, Retry, DLQ e Reprocessamento

**Branch**: `003-resilience` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

O `WorkflowEngine` passa a classificar falhas (`ErrorClassifier`) e, conforme a política (`RetryPolicy` por tipo de operação), reagenda a operação via outbox (`available_at`), marca a operação como `FAILED` (permanente) ou envia à DLQ do domínio (esgotada). Operações são roteadas para a fila do seu domínio. Um `ReprocessHandler` e `GET /v1/dead-letters` completam a operação manual. O provider simulado e a origem de teste ganham injeção de falhas.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: existentes (EF Core/Npgsql, RabbitMQ.Client, AWSSDK.S3); nenhuma nova
**Storage**: PostgreSQL — nova tabela `dead_letter_entry`; `operation.status` ganha `RETRY_PENDING`/`DLQ`; coluna `operation.error_class`
**Testing**: xUnit unit (política, classificador) + Testcontainers (fluxos de retry/DLQ/reprocesso com cronogramas curtos)
**Target Platform**: Docker Compose (inalterado)
**Project Type**: web-service + worker
**Performance Goals**: recuperação transitória sem intervenção; reprocesso → COMPLETED < 30 s
**Constraints**: at-least-once + idempotência; mensagens só com referências
**Scale/Scope**: 4 filas de comando + 4 DLQs; 2 consumidores no Worker

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Classificação em termos neutros (`OperationException`); nenhum tipo de fornecedor. PASS |
| II System of record | Falhas e reprocessos preservados no journal/dead letters. PASS |
| III Idempotência | Retry/reprocesso reaproveita a mesma operação; inbox e status guard evitam efeitos duplicados. PASS |
| IV Outbox/filas por domínio | Reagendamento e DLQ publicados via outbox; fila e DLQ por domínio. PASS |
| V Estados/Retry/DLQ | Backoff exponencial + jitter, classificação TRANSIENT/PERMANENT/UNKNOWN, retomada a partir da operação. PASS |
| VI Auditoria | `OPERATION_RETRY_SCHEDULED`, `OPERATION_FAILED`, `OPERATION_DEAD_LETTERED`, `OPERATION_REPROCESS_REQUESTED`. PASS |
| VII Segurança/testes | DLQ só com ids; testes dos fluxos críticos. PASS |

Sem violações. Circuit breaker/bulkhead permanecem Fase 2 (constitution V). Re-check pós-design: PASS.

## Project Structure

```text
src/
├── Orchestrator.Domain/ StateMachines/Statuses.cs (OperationStatus +RETRY_PENDING, DLQ), Entities/DeadLetterEntry.cs, ErrorClass
├── Orchestrator.Application/Resilience/ RetryOptions, RetryPolicy, ErrorClassifier, OperationDomains
├── Orchestrator.Application/Workflow/WorkflowEngine.cs (refatorado) · ReprocessHandler · DeadLetterQueries
├── Orchestrator.Infrastructure/Messaging/ Topology (4 filas + 4 DLQs), OutboxPublisher (exchange por fila), OperationConsumer (por fila)
├── Orchestrator.Infrastructure/Providers/FakeProviderAdapter.cs (SIM-FLAKY-n-, SIM-DOWN-, SIM-UNKNOWN-)
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddResilience
├── Orchestrator.Api/Endpoints/ ResilienceEndpoints (retry, dead-letters)
tests/ UnitTests (RetryPolicy, ErrorClassifier, OperationDomains) · IntegrationTests (RetryTests, DeadLetterTests, ReprocessTests)
```

**Structure Decision**: mesma Clean Architecture; política e classificação ficam na Application (puras e testáveis).
