# Tasks: Núcleo de Orquestração de Assinaturas

**Input**: `/specs/001-core-orchestration/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: Incluídos para fluxos críticos (idempotência, estados, outbox/inbox, journal), escritos antes da implementação.

## Phase 1: Setup

- [X] T001 Criar solução `DigitalSignature.sln` e projetos net8.0 em `src/Orchestrator.Domain`, `src/Orchestrator.Application`, `src/Orchestrator.Infrastructure`, `src/Orchestrator.Api`, `src/Orchestrator.Worker`, `tests/Orchestrator.UnitTests`, `tests/Orchestrator.IntegrationTests` com referências conforme plan.md
- [X] T002 [P] Adicionar `.gitignore` (bin/obj/.vs), `Directory.Build.props` (nullable, warnings) e `global.json` fixando SDK 8 na raiz
- [X] T003 [P] Criar `deploy/Api.Dockerfile`, `deploy/Worker.Dockerfile` e `docker-compose.yml` com postgres, rabbitmq (management), api (porta 8080), worker e healthchecks

## Phase 2: Foundational

- [X] T004 [P] Enums e máquinas de estado em `src/Orchestrator.Domain/StateMachines/` (`BusinessStatus`, `OperationalStatus`, `BusinessStateMachine`, `OperationalStateMachine`) conforme data-model.md
- [X] T005 [P] Entidades de domínio em `src/Orchestrator.Domain/Entities/` (`SignatureProcess`, `Signer`, `Operation`, `JournalEvent`, `OutboxEvent`, `IdempotencyRecord`, `ProviderProcess`) e `OperationType`
- [X] T006 `OrchestratorDbContext` e mapeamentos (JSONB, snake_case, token de concorrência `version`) em `src/Orchestrator.Infrastructure/Persistence/`
- [X] T007 Migration inicial com trigger de journal append-only (bloqueia UPDATE/DELETE) e índice parcial da outbox em `src/Orchestrator.Infrastructure/Persistence/Migrations/`
- [X] T008 [P] Gerador de ids (`sig_`, `op_`, `evt_`) e `IClock` em `src/Orchestrator.Application/Abstractions/`
- [X] T009 [P] Middleware de `X-Correlation-Id` e tratamento de erros `application/problem+json` em `src/Orchestrator.Api/Middleware/`
- [X] T010 [P] Máscara de dados sensíveis para logs (`SensitiveMasker`) em `src/Orchestrator.Application/Security/`
- [X] T011 Serviço de journal (`IJournal.Append`) e de outbox (`IOutbox.Enqueue`) que participam da transação corrente em `src/Orchestrator.Infrastructure/Messaging/`

## Phase 3: User Story 1 — Criar processo idempotente (P1)

**Independent Test**: criar duas vezes com a mesma chave → mesmo processId; payload diferente → 409.

- [X] T012 [P] [US1] Testes unitários das máquinas de estado (transições válidas e inválidas) em `tests/Orchestrator.UnitTests/StateMachineTests.cs`
- [X] T013 [P] [US1] Testes unitários de validação do payload e do hash canônico em `tests/Orchestrator.UnitTests/CreateProcessValidationTests.cs`
- [X] T014 [P] [US1] Testes de integração (Testcontainers Postgres): criação 202, replay 200, conflito 409, sem chave 400, 50 concorrentes → 1 processo, em `tests/Orchestrator.IntegrationTests/CreateProcessTests.cs` (+ `TestFixture.cs`)
- [X] T015 [US1] Implementar validação do payload (campos, CPF, tipos de assinatura, rejeição de `provider`/`providers`) em `src/Orchestrator.Application/Processes/CreateProcessValidator.cs`
- [X] T016 [US1] Implementar `CreateProcessHandler` (insert-first na idempotency, processo+signers+operação DOCUMENT_DOWNLOAD+journal PROCESS_CREATED+outbox na mesma transação; retenção 24h) em `src/Orchestrator.Application/Processes/CreateProcessHandler.cs`
- [X] T017 [US1] Endpoint (logs com `SensitiveMasker`, FR-015) `POST /v1/signature-processes` com `Location`, 202/200/400/409 em `src/Orchestrator.Api/Endpoints/SignatureProcessEndpoints.cs` e `Program.cs` (migrations no start, OpenAPI)

## Phase 4: User Story 2 — Fluxo até COMPLETED com provider simulado (P1)

**Independent Test**: processo criado chega a COMPLETED; evento duplicado ignorado.

- [X] T018 [P] [US2] Testes unitários do `FakeProviderAdapter` (idempotência por referência externa, modos assina/rejeita/falha) em `tests/Orchestrator.UnitTests/FakeProviderAdapterTests.cs`
- [X] T019 [P] [US2] Testes de integração do fluxo ponta a ponta, inbox (mensagem duplicada), outbox (broker indisponível depois volta) e asserção de que mensagens contêm só ids e correlationId (FR-014, FR-018) em `tests/Orchestrator.IntegrationTests/WorkflowTests.cs`
- [X] T020 [US2] Interface `IProviderAdapter` e DTOs neutros (createProcess, sendDocument, addSigner, getStatus, cancel) em `src/Orchestrator.Application/Providers/IProviderAdapter.cs`
- [X] T021 [US2] `FakeProviderAdapter` com tabela `provider_process` (guarda metadata nativa em JSONB + status normalizado, FR-013), assinatura automática com atraso configurável e modos de falha/rejeição em `src/Orchestrator.Infrastructure/Providers/FakeProviderAdapter.cs`
- [X] T022 [US2] `WorkflowEngine`: executa uma operação, aplica transições de negócio/operacional, cria a próxima operação, journaliza e enfileira outbox (inclui reagendamento de PROVIDER_STATUS_CHECK via `available_at`) em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`
- [X] T023 [US2] Handlers das operações (DOCUMENT_DOWNLOAD, DOCUMENT_STORE, PROVIDER_*, SIGNED_DOCUMENT_*) em `src/Orchestrator.Application/Workflow/Handlers/`
- [X] T024 [US2] Outbox publisher (propaga correlationId/causationId nos headers da mensagem) (SKIP LOCKED, confirms, `available_at`) e topologia RabbitMQ (exchange, fila `signature-provider`, DLX) em `src/Orchestrator.Infrastructure/Messaging/`
- [X] T025 [US2] Consumer com inbox (PK consumer+messageId na mesma transação do efeito) em `src/Orchestrator.Infrastructure/Messaging/OperationConsumer.cs`
- [X] T026 [US2] Hosted services e DI do Worker em `src/Orchestrator.Worker/Program.cs`

## Phase 5: User Story 3 — Consultas (P2)

**Independent Test**: após concluir um processo, os endpoints de consulta retornam dados esperados.

- [X] T027 [P] [US3] Testes de integração dos GETs (processo, status, operations, events, paginação, 404) em `tests/Orchestrator.IntegrationTests/QueryTests.cs`
- [X] T028 [US3] Queries e DTOs (`ProcessQueries`) em `src/Orchestrator.Application/Processes/ProcessQueries.cs`
- [X] T029 [US3] Endpoints `GET /v1/signature-processes/{id}`, `/status`, `/operations`, `/events` em `src/Orchestrator.Api/Endpoints/SignatureProcessEndpoints.cs`

## Phase 6: User Story 4 — Cancelamento (P3)

**Independent Test**: cancelar antes da conclusão → CANCELLED; terminal → 409; repetido → idempotente.

- [X] T030 [P] [US4] Testes de integração de cancelamento (incl. operações pendentes ignoradas e corrida com avanço) em `tests/Orchestrator.IntegrationTests/CancelTests.cs`
- [X] T031 [US4] `CancelProcessHandler` (concorrência otimista, journal, operações pendentes → CANCELLED) em `src/Orchestrator.Application/Processes/CancelProcessHandler.cs`
- [X] T032 [US4] Endpoint `POST /v1/signature-processes/{id}/cancel` em `src/Orchestrator.Api/Endpoints/SignatureProcessEndpoints.cs` e consumer ignorando operações canceladas

## Phase 7: Polish

- [X] T033 [P] Teste de journal append-only (UPDATE/DELETE falham) em `tests/Orchestrator.IntegrationTests/JournalTests.cs`
- [X] T034 [P] `scripts/smoke-test.sh` contra o compose (cria, replay, aguarda COMPLETED, consulta, cancela)
- [X] T035 [P] `README.md` com execução, endpoints e arquitetura
- [X] T036 Executar `dotnet build`, `dotnet test`, `docker compose up` e quickstart; corrigir falhas

## Dependencies

Setup → Foundational → US1 → US2 → US3 e US4 (independentes entre si, ambos após US2). Dentro da fase: testes [P] antes da implementação.

## Parallel Example (US1)

T012, T013, T014 em paralelo; depois T015 → T016 → T017.

## Implementation Strategy

MVP = US1 + US2 (fluxo completo). US3 e US4 incrementais.
