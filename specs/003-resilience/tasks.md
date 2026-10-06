# Tasks: Resiliência, Retry, DLQ e Reprocessamento

**Input**: `/specs/003-resilience/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: incluídos para fluxos críticos (política, classificação, retry, DLQ, reprocesso), antes da implementação.

## Phase 1: Setup

- [X] T001 [P] Adicionar `OperationStatus.RETRY_PENDING` e `DLQ` em `src/Orchestrator.Domain/StateMachines/Statuses.cs`, enum `ErrorClass` (TRANSIENT, PERMANENT, UNKNOWN) e entidade `DeadLetterEntry` (id `dlq_*`; operation_type max 60; domain max 40; queue max 80; error_class max 20; reason max 500; resolved_by max 100) em `src/Orchestrator.Domain/Entities/DeadLetterEntry.cs`; coluna `ErrorClass` em `Operation`
- [X] T002 Mapeamento EF (`dead_letter_entry`, índice (domain, resolved_at), `operation.error_class`), `IOrchestratorDb.DeadLetters` e migration `AddResilience` em `src/Orchestrator.Infrastructure/Persistence/`

## Phase 2: Foundational

- [X] T003 [P] `RetryOptions` (Jitter 0.2, UnknownMaxAttempts 3, Default{MaxAttempts 8, DelaysSeconds, BaseDelaySeconds 5, Multiplier 6, MaxDelaySeconds 21600}, Operations por tipo) e `RetryPolicy` (`MaxAttemptsFor(type)`, `NextDelay(type, attempt, random)`) em `src/Orchestrator.Application/Resilience/RetryPolicy.cs`
- [X] T004 [P] `ErrorClassifier` (OperationException, HttpRequestException/Timeout/IO → TRANSIENT; InvalidTransition/Validation → PERMANENT; demais UNKNOWN; `FromHttpStatus`) em `src/Orchestrator.Application/Resilience/ErrorClassifier.cs`; `OperationException` passa a expor `Classification`
- [X] T005 [P] `OperationDomains` (tipo→domínio/fila, fila→DLQ) em `src/Orchestrator.Application/Resilience/OperationDomains.cs`; `EventRecorder.EnqueueOperation` usa a fila do domínio e `EnqueueDeadLetter`
- [X] T006 Topologia RabbitMQ com 4 filas + 4 DLQs, `OutboxPublisher` publicando em `orchestrator.dlx` para filas `*-dlq`, e `OperationConsumer` parametrizado por fila (Worker consome `signature-provider` e `artifact`) em `src/Orchestrator.Infrastructure/Messaging/RabbitMq.cs` e `src/Orchestrator.Worker/Program.cs`

## Phase 3: User Story 1 — Recuperação automática de falhas transitórias (P1)

**Independent Test**: provider falha 2× de forma transitória e o processo chega a COMPLETED.

- [X] T007 [P] [US1] Testes unitários de `RetryPolicy` (cronograma padrão, jitter entre 0,8 e 1,2, tentativa 1 imediata, política por tipo, fallback exponencial, sem valores negativos) em `tests/Orchestrator.UnitTests/RetryPolicyTests.cs`
- [X] T008 [P] [US1] Testes unitários de `ErrorClassifier` (408/429/5xx transitórios, 4xx permanentes, rede/timeout, desconhecido) e `OperationDomains` em `tests/Orchestrator.UnitTests/ErrorClassifierTests.cs`
- [X] T009 [P] [US1] Estender a origem de teste do `TestFixture` com `/flaky/{n}` (503 nas n primeiras requisições) e `/forbidden` (403) e criar testes de integração: `SIM-FLAKY-2-` recupera, origem `/flaky/2` (503) recupera, `nextRetryAt` no futuro e estado `RETRY_PENDING`, cancelamento durante `RETRY_PENDING` (cronogramas curtos via configuração) em `tests/Orchestrator.IntegrationTests/RetryTests.cs`
- [X] T010 [US1] Refatorar `WorkflowEngine` e `CancelProcessHandler` (cancelar também operações `RETRY_PENDING`, FR-015): SaveChanges fora do try do handler, classificar falha, reagendar transitório (op RETRY_PENDING + outbox `available_at` + processo RETRY_PENDING + journal), aceitar status NOT_STARTED/RETRY_PENDING, política via `RetryPolicy`, `Next` usa `maxAttempts` por tipo, em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`
- [X] T011 [US1] Injeção de falhas no `FakeProviderAdapter` (`SIM-FLAKY-<n>-` via `Attempt` no request, `SIM-DOWN-`, `SIM-UNKNOWN-`) em `src/Orchestrator.Infrastructure/Providers/FakeProviderAdapter.cs` e `src/Orchestrator.Application/Providers/IProviderAdapter.cs`; `HttpDocumentFetcher` usa `ErrorClassifier.FromHttpStatus`

## Phase 4: User Story 2 — Classificação e DLQ (P1)

**Independent Test**: permanente → MANUAL_ACTION; transitório persistente e desconhecido → DLQ listada na API.

- [X] T012 [P] [US2] Testes de integração: permanente (404 e 403) → FAILED/MANUAL_ACTION sem retry; `SIM-DOWN-` e `SIM-UNKNOWN-` → DLQ com mensagem só de referências na fila `*-dlq`; DLQ do domínio correto (artifact vs signature-provider); `GET /v1/dead-letters` com filtros e paginação; mensagem malformada → DLQ, em `tests/Orchestrator.IntegrationTests/DeadLetterTests.cs`
- [X] T013 [US2] No engine: DLQ (op DLQ, `dead_letter_entry`, processo DLQ, journal `OPERATION_DEAD_LETTERED`, outbox na `*-dlq` com payload `{processId, operationId, deadLetterId, domain, errorClass}`), UNKNOWN com `UnknownMaxAttempts` em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`
- [X] T014 [US2] `DeadLetterQueries` e endpoint `GET /v1/dead-letters` em `src/Orchestrator.Application/Resilience/DeadLetterQueries.cs` e `src/Orchestrator.Api/Endpoints/ResilienceEndpoints.cs`

## Phase 5: User Story 3 — Reprocessamento (P1)

**Independent Test**: operação em DLQ corrigida e reprocessada; anteriores não repetem; auditoria no journal.

- [X] T015 [P] [US3] Testes de integração: retry sem `operationId` (falha mais recente), com `operationId`, operações concluídas não repetem (artefatos/operações iguais), terminal → 409, outro processo/inexistente → 404, dois retries concorrentes → um 200 e um 409, dead letter resolvida, journal `OPERATION_REPROCESS_REQUESTED`, em `tests/Orchestrator.IntegrationTests/ReprocessTests.cs`
- [X] T016 [US3] `ReprocessHandler` (transação única, concorrência por `version`) em `src/Orchestrator.Application/Workflow/ReprocessHandler.cs` e endpoint `POST /v1/signature-processes/{id}/retry` em `src/Orchestrator.Api/Endpoints/ResilienceEndpoints.cs`; mapear exceções (404/409) já existentes

## Phase 6: Polish

- [X] T017 [P] Configuração `Retry` em `appsettings.json` da API e do Worker e variáveis de exemplo no `docker-compose.yml` (cronograma curto para demonstração via `Retry__Default__DelaysSeconds__N`)
- [X] T018 [P] Atualizar `scripts/smoke-test.sh` (SIM-FLAKY recupera; SIM-DOWN → DLQ; retry endpoint) e `README.md`
- [X] T019 Executar `dotnet build`, `dotnet test`, `docker compose up` e smoke; corrigir falhas

## Dependencies

Setup → Foundational → US1 → US2 → US3 (US3 depende de DLQ/FAILED de US2). Testes [P] antes da implementação de cada story.

## Implementation Strategy

MVP = US1 + US2 (retry e DLQ); US3 completa a operação manual.
