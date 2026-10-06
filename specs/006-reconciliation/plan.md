# Implementation Plan: Reconciliação Básica com o Provider

**Branch**: `006-reconciliation` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

Um `ReconciliationWorker` (BackgroundService do Worker) seleciona periodicamente processos aguardando o provider cuja última atividade/conferência passou do limite e chama `ReconciliationService.ReconcileAsync`. O serviço consulta o `IProviderAdapter`, compara com o estado interno e, havendo divergência, corrige pela máquina de estados, emite callbacks, cria `SIGNED_DOCUMENT_DOWNLOAD`, neutraliza o acompanhamento obsoleto e audita (journal + `reconciliation_record`). A mesma operação atende `POST /reconcile` e o histórico `GET /reconciliations`. Concorrência por token otimista `version`.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: existentes; sem pacotes novos
**Storage**: PostgreSQL — `signature_process.last_reconciled_at`, `reconciliation_record`
**Testing**: xUnit unit (InMemory + `FakeProviderAdapter` com relógio controlável) + Testcontainers (processo divergente inserido no banco; worker de reconciliação rápido)
**Target Platform**: Docker Compose (Worker passa a hospedar o reconciliador)
**Project Type**: web-service + worker
**Performance Goals**: divergência corrigida em até 2 ciclos após o limite
**Constraints**: idempotente; sem registros no caso consistente; erro do provider não derruba o ciclo
**Scale/Scope**: lote 50 por ciclo; várias instâncias seguras

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Só usa `IProviderAdapter.GetStatusAsync` e status normalizados. PASS |
| II System of record | Estado interno é atualizado e preservado com histórico de reconciliação. PASS |
| III Idempotência | Reexecução encontra consistência; operação de download criada uma vez; token `version` evita corrida. PASS |
| IV Outbox | Mudança de estado, journal, callback e novas operações na mesma transação com outbox. PASS |
| V Operações/Reconciliação | Reconciliation Worker complementa o acompanhamento (webhook + reconciliação); operações obsoletas neutralizadas. PASS |
| VI Auditoria | `RECONCILIATION_DISCREPANCY_FOUND`, `RECONCILIATION_CORRECTED`, `RECONCILIATION_PROVIDER_ERROR` + registro consultável (base para a métrica). PASS |
| VII Segurança/testes | Fluxo crítico coberto por testes unitários e de integração. PASS |

Sem violações. Re-check pós-design: PASS.

## Project Structure

```text
src/
├── Orchestrator.Domain/Entities/ SignatureProcess.LastReconciledAt, ReconciliationRecord
├── Orchestrator.Application/Reconciliation/ ReconciliationOptions, ReconciliationService, ReconciliationQueries, ReconcileResult
├── Orchestrator.Infrastructure/Reconciliation/ ReconciliationWorker
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddReconciliation
├── Orchestrator.Api/Endpoints/ ReconciliationEndpoints
tests/ UnitTests (ReconciliationServiceTests) · IntegrationTests (ReconciliationTests)
```

**Structure Decision**: mesma Clean Architecture; regras de comparação/correção na Application, agendamento na Infrastructure.
