# Implementation Plan: Identity Proofing por Capabilities

**Branch**: `005-identity-proofing` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

Novo bounded context dentro da mesma solução: `ProofingSession` + `IdentityValidation`, API `/v1/proofing-sessions`, evidências no mesmo artifact store (tipos novos) e execução assíncrona de `IDENTITY_VALIDATION` pela mesma máquina de operações (fila `identity-proofing`, retry/DLQ/reprocesso da spec 003) atrás de `IIdentityProofingAdapter` (provider simulado). Mutações da sessão são serializadas por `SELECT ... FOR UPDATE` numa transação explícita; o resultado agregado é calculado ao fechar a sessão. Sem dependência de processo de assinatura.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: existentes; sem pacotes novos
**Storage**: PostgreSQL — `proofing_session`, `identity_validation`; reuso de `operation`, `journal_event`, `outbox_event`, `inbox_message`, `dead_letter_entry` e `artifact` (agregado genérico por id)
**Testing**: xUnit unit (capacidades, agregação, adapter simulado) + Testcontainers (fluxos completos, concorrência, retenção)
**Target Platform**: Docker Compose (worker consome também a fila `identity-proofing`)
**Project Type**: web-service + worker
**Performance Goals**: sessão sem evidência conclui < 15 s
**Constraints**: nunca expor conteúdo de evidência nem CPF completo; mensagens só com ids
**Scale/Scope**: até 11 validações por sessão, evidências ≤ 5 MB

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Capacidades no contrato, `IIdentityProofingAdapter` neutro; nada de fornecedor (campos provider/providers rejeitados). PASS |
| II System of record / bounded contexts | Contexto próprio, utilizável sem assinatura; evidências com SHA-256, tipo e tamanho; resultado persistido. PASS |
| III Idempotência | `Idempotency-Key` obrigatório (namespace `proofing:`); evidência única por tipo; criação de validações única sob lock; inbox. PASS |
| IV Outbox/filas | Operações criadas na mesma transação do estado e publicadas via outbox na fila `identity-proofing` com DLQ própria. PASS |
| V Operações/retry | `IDENTITY_VALIDATION` independente, retry/DLQ/reprocesso da spec 003. PASS |
| VI Auditoria | Eventos `PROOFING_SESSION_CREATED`, `IDENTITY_VALIDATION_REQUESTED`, `EVIDENCE_RECEIVED`, `IDENTITY_VALIDATED`, `PROOFING_SESSION_COMPLETED`, `EVIDENCE_DELETED`. PASS |
| VII/Segurança e dados | Biometria como dado sensível: sem download, CPF mascarado, exclusão e retenção explícitas, sem conteúdo em logs. PASS |

Sem violações. Re-check pós-design: PASS.

## Project Structure

```text
src/
├── Orchestrator.Domain/Entities/ ProofingSession, IdentityValidation, enums; ArtifactType (+DOCUMENT_FRONT, DOCUMENT_BACK, SELFIE)
├── Orchestrator.Application/Identity/ IdentityCapabilities, IIdentityProofingAdapter, ProofingService, IdentityValidationRunner, ReprocessIdentityHandler, ProofingQueries, EvidencePurger
├── Orchestrator.Application/Workflow/WorkflowEngine.cs (ramo IDENTITY_VALIDATION; HandleFailureAsync com processo opcional)
├── Orchestrator.Application/Artifacts/ (ArtifactKeys, IArtifactStore.DeleteAsync)
├── Orchestrator.Infrastructure/Identity/ FakeIdentityAdapter, EvidenceRetentionService (hospedado no Worker)
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddIdentityProofing (inclui remoção da FK operation→signature_process)
├── Orchestrator.Api/Endpoints/ ProofingEndpoints
tests/ UnitTests (IdentityCapabilitiesTests, FakeIdentityAdapterTests, ProofingAggregationTests) · IntegrationTests (ProofingTests, ProofingResilienceTests, ProofingRetentionTests)
```

**Structure Decision**: mesma Clean Architecture; bounded context separado por namespace/tabelas e API próprios, reutilizando infraestrutura de mensageria e operações.
