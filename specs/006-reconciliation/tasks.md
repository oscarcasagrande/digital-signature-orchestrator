# Tasks: Reconciliação Básica com o Provider

**Input**: `/specs/006-reconciliation/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: incluídos para fluxos críticos (comparação, correção, idempotência, concorrência), antes da implementação.

## Phase 1: Setup

- [X] T001 [P] Coluna `LastReconciledAt` em `SignatureProcess` e entidade `ReconciliationRecord` (id `rec_*`; trigger max 20; internal_status/provider_status/resulting_status max 40; outcome max 20; details jsonb) em `src/Orchestrator.Domain/Entities/`
- [X] T002 Mapeamento EF (`reconciliation_record` com índices (process_id, created_at) e (outcome, created_at); `last_reconciled_at`), `IOrchestratorDb.ReconciliationRecords` e migration `AddReconciliation` em `src/Orchestrator.Infrastructure/Persistence/`
- [X] T003 [P] `ReconciliationOptions` (Enabled true, IntervalSeconds 60, StaleAfterSeconds 120, BatchSize 50) em `src/Orchestrator.Application/Reconciliation/ReconciliationOptions.cs`

## Phase 2: User Story 1 — Corrigir divergências automaticamente (P1)

**Independent Test**: processo em SIGNATURE_IN_PROGRESS cujo provider já assinou, sem acompanhamento ativo, chega a COMPLETED pelo reconciliador.

- [X] T004 [P] [US1] Testes unitários de `ReconciliationService` com InMemory e `FakeProviderAdapter` com relógio controlável (assinado → SIGNED + download + callback; parcial; rejeitado; cancelado; READY_FOR_SIGNATURE passando por SIGNATURE_IN_PROGRESS; pendente = consistente; provider à frente; neutralização de PROVIDER_STATUS_CHECK e resolução de dead letters; sem registro/journal no consistente; erro do provider) em `tests/Orchestrator.UnitTests/ReconciliationServiceTests.cs`
- [X] T005 [P] [US1] Ajustar a fixture (`Reconciliation:Enabled`, intervalo 0,3 s, obsolescência 0,5 s, worker hospedado) e criar testes de integração (processo divergente inserido no banco com `provider_process` e original no store: worker leva a COMPLETED com artefatos; callback SIGNED/COMPLETED entregue; acompanhamento em DLQ neutralizado; consistente sem alterações; erro do provider não derruba o worker) em `tests/Orchestrator.IntegrationTests/ReconciliationTests.cs`
- [X] T006 [US1] `ReconciliationService.ReconcileAsync` (loop de concorrência otimista com `version`, comparação via `IProviderAdapter.GetStatusAsync`, correção pela máquina de estados, `CallbackEmitter`, criação única de `SIGNED_DOCUMENT_DOWNLOAD`, neutralização do acompanhamento, `last_reconciled_at`, registro e journal) em `src/Orchestrator.Application/Reconciliation/ReconciliationService.cs`
- [X] T007 [US1] `ReconciliationWorker` (BackgroundService: seleciona candidatos por `COALESCE(last_reconciled_at, updated_at)`, escopo por processo, captura de exceções por processo) em `src/Orchestrator.Infrastructure/Reconciliation/ReconciliationWorker.cs`; registrar no DI e no `src/Orchestrator.Worker/Program.cs` (respeitando `Enabled`)

## Phase 3: User Story 2 — Reconciliação manual e histórico (P2)

**Independent Test**: reconciliar um processo divergente e um consistente e consultar o histórico.

- [X] T008 [P] [US2] Testes de integração (`POST /reconcile`: CORRECTED, CONSISTENT, NOT_APPLICABLE, 409 terminal, 404; `GET /reconciliations`; journal com ator; registros só para correções e chamadas manuais) em `tests/Orchestrator.IntegrationTests/ReconciliationApiTests.cs`
- [X] T009 [US2] `ReconciliationQueries` e endpoints `POST /v1/signature-processes/{id}/reconcile` e `GET /v1/signature-processes/{id}/reconciliations` em `src/Orchestrator.Application/Reconciliation/ReconciliationQueries.cs` e `src/Orchestrator.Api/Endpoints/ReconciliationEndpoints.cs`

## Phase 4: User Story 3 — Concorrência e várias instâncias (P2)

**Independent Test**: reconciliações concorrentes do mesmo processo divergente produzem uma única correção.

- [X] T010 [P] [US3] Testes de integração (10 reconciliações concorrentes: 1 correção, 1 operação de download, 1 callback por estado; reconciliação concorrente com o acompanhamento normal sem transição inválida; reexecução idempotente) em `tests/Orchestrator.IntegrationTests/ReconciliationConcurrencyTests.cs`
- [X] T011 [US3] Ajustes decorrentes dos testes de concorrência (releitura/reavaliação, tratamento de violação do índice único de operação) em `src/Orchestrator.Application/Reconciliation/ReconciliationService.cs`

## Phase 5: Polish

- [X] T012 [P] Configuração `Reconciliation` em `appsettings.json` da API e do Worker e valores de demonstração (10 s / 20 s) no `docker-compose.yml`
- [X] T013 [P] Atualizar `scripts/smoke-test.sh` (reconciliação manual e histórico) e `README.md`
- [X] T014 Executar `dotnet build`, `dotnet test`, `docker compose up` e smoke; corrigir falhas

## Dependencies

Setup → US1 → US2 e US3 (independentes entre si após US1). Testes [P] antes da implementação.

## Implementation Strategy

MVP = US1 (correção automática). US2 adiciona operação manual e histórico; US3 endurece a concorrência.
