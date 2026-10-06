# Tasks: Identity Proofing por Capabilities

**Input**: `/specs/005-identity-proofing/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: incluídos para fluxos críticos (pré-requisitos, agregação, concorrência, resiliência, retenção), antes da implementação.

## Phase 1: Setup

- [X] T001 [P] Entidades `ProofingSession` (external_id max 200; status max 30; result max 20; subject jsonb) e `IdentityValidation` (capability max 40; status max 20; score 0 a 1; details jsonb; provider_code max 40) e enums em `src/Orchestrator.Domain/Entities/Proofing.cs`; `ArtifactType` ganha `DOCUMENT_FRONT`, `DOCUMENT_BACK`, `SELFIE`
- [X] T002 Mapeamento EF (`proofing_session`, `identity_validation` com único (session_id, capability)), `IOrchestratorDb.ProofingSessions/IdentityValidations`, ignorar `SignatureProcess.Operations` (remove a FK de `operation`), `ArtifactKeys` para os tipos novos, e migration `AddIdentityProofing` em `src/Orchestrator.Infrastructure/Persistence/` e `src/Orchestrator.Application/Artifacts/Artifacts.cs`
- [X] T003 [P] `IArtifactStore.DeleteAsync` (S3 `DeleteObject` idempotente; store em memória dos testes) em `src/Orchestrator.Application/Artifacts/Artifacts.cs` e `src/Orchestrator.Infrastructure/Storage/S3ArtifactStore.cs`
- [X] T004 [P] `IdentityOptions` (MaxEvidenceBytes 5242880, EvidenceRetentionDays 30, RetentionSweepMinutes 60) em `src/Orchestrator.Application/Identity/IdentityOptions.cs`

## Phase 2: Foundational

- [X] T005 [P] Testes unitários de `IdentityCapabilities` (conjunto conhecido, pré-requisitos de evidência e de sujeito, `IsReady`) e da validação do pedido de sessão em `tests/Orchestrator.UnitTests/IdentityCapabilitiesTests.cs`
- [X] T006 [P] Testes unitários do `FakeIdentityAdapter` (marcadores, finais de CPF/telefone, e-mail, deviceId, scores determinísticos, `IDENTITY_RISK`, falhas por prefixo) e da agregação de resultado em `tests/Orchestrator.UnitTests/FakeIdentityAdapterTests.cs`
- [X] T007 `IdentityCapabilities` (capacidades, evidências exigidas, dados do sujeito, `IsReady`) e `IIdentityProofingAdapter` com DTOs neutros em `src/Orchestrator.Application/Identity/`
- [X] T008 `FakeIdentityAdapter` (resultado determinístico por marcadores e prefixos `SIM-`) em `src/Orchestrator.Infrastructure/Identity/FakeIdentityAdapter.cs`
- [X] T009 Fila `identity-proofing` nas filas padrão do Worker e DI de opções/serviços em `src/Orchestrator.Infrastructure/Messaging/RabbitMq.cs` e `src/Orchestrator.Infrastructure/DependencyInjection.cs`

## Phase 3: User Story 1 — Sessão e resultado (P1)

**Independent Test**: sessão só com `PERSON_DATA` chega a APPROVED sem processo de assinatura.

- [X] T010 [P] [US1] Testes de integração (criação 202/200/409, validações de entrada, rejeição de `provider`, `PERSON_DATA` concluindo sozinho, `APPROVED`/`REJECTED` por obrigatoriedade, `IDENTITY_RISK` por último, CPF mascarado, journal, e criação de processo de assinatura com `identityProofing` continuando independente de sessões, FR-015) em `tests/Orchestrator.IntegrationTests/ProofingSessionTests.cs`
- [X] T011 [US1] `ProofingService.CreateAsync` (validação, idempotência `proofing:`, sessão + validações + operações prontas + journal, tudo na mesma transação e sob lock) em `src/Orchestrator.Application/Identity/ProofingService.cs`
- [X] T012 [US1] `IdentityValidationRunner` (executa a validação via adapter, atualiza a validação, journal `IDENTITY_VALIDATED`, cria `IDENTITY_RISK`, fecha a sessão e calcula o resultado) em `src/Orchestrator.Application/Identity/IdentityValidationRunner.cs`
- [X] T013 [US1] `WorkflowEngine`: ramo `IDENTITY_VALIDATION` (transação explícita com `FOR UPDATE` na sessão, recarga da operação, runner) e `HandleFailureAsync` com processo opcional e atualização da validação (`PENDING` em retry, `ERROR` em falha permanente/DLQ) em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`
- [X] T014 [US1] `ProofingQueries` (sessão, resultado, eventos) e endpoints `POST /v1/proofing-sessions`, `GET /{id}`, `GET /{id}/result`, `GET /{id}/events` em `src/Orchestrator.Application/Identity/ProofingQueries.cs` e `src/Orchestrator.Api/Endpoints/ProofingEndpoints.cs`

## Phase 4: User Story 2 — Evidências (P1)

**Independent Test**: LIVENESS e FACE_MATCH executam conforme selfie e documento chegam.

- [X] T015 [P] [US2] Testes de integração (envio de documento e selfie, hash/tamanho/tipo, duplicata 409, tipo/base64/tamanho inválidos, `FACE_MATCH` só após selfie+documento, marcadores `FAKE_SPOOF`/`FAKE_FACE_MISMATCH`, opcional falhando mantém APPROVED, concorrência de 6 validações e envio paralelo de evidências sem duplicata) em `tests/Orchestrator.IntegrationTests/ProofingEvidenceTests.cs`
- [X] T016 [US2] `ProofingService.ReceiveEvidenceAsync` (valida, armazena via `ArtifactService`, journal `EVIDENCE_RECEIVED`, cria operações das validações que ficaram prontas, atualiza estado da sessão, sob lock) e endpoints `POST /documents` e `POST /biometrics` em `src/Orchestrator.Application/Identity/ProofingService.cs` e `src/Orchestrator.Api/Endpoints/ProofingEndpoints.cs`

## Phase 5: User Story 3 — Resiliência, auditoria e proteção de dados (P2)

**Independent Test**: transitória recupera, permanente/DLQ exigem ação, retry reprocessa, evidências são excluídas.

- [X] T017 [P] [US3] Testes de integração (`SIM-FLAKY-2-` recupera, `SIM-DOWN-` vai à DLQ `identity-proofing-dlq` e a `/v1/dead-letters?domain=identity-proofing`, `SIM-FAIL-` vira `ERROR`, retry com e sem `operationId`, 409/404, sessão fica pendente) em `tests/Orchestrator.IntegrationTests/ProofingResilienceTests.cs`
- [X] T018 [P] [US3] Testes de integração (exclusão manual só em sessão concluída, objetos e registros removidos, resultado permanece, journal com hashes; retenção por `EvidencePurger` com sessões antigas; nenhuma resposta com CPF completo ou conteúdo) em `tests/Orchestrator.IntegrationTests/ProofingRetentionTests.cs`
- [X] T019 [US3] `ReprocessIdentityHandler` e endpoint `POST /v1/proofing-sessions/{id}/retry` em `src/Orchestrator.Application/Identity/ReprocessIdentityHandler.cs`
- [X] T020 [US3] `EvidencePurger` (por sessão e por retenção), endpoint `DELETE /v1/proofing-sessions/{id}/evidence` e `EvidenceRetentionService` hospedado no Worker em `src/Orchestrator.Application/Identity/EvidencePurger.cs`, `src/Orchestrator.Infrastructure/Identity/EvidenceRetentionService.cs` e `src/Orchestrator.Worker/Program.cs`

## Phase 6: Polish

- [X] T021 [P] Atualizar `scripts/smoke-test.sh` (sessão sem assinatura, evidências, resultado, rejeição por `FAKE_SPOOF`, exclusão) e `README.md`
- [X] T022 [P] Criar `docs/DATA-PROTECTION.md` com a política explícita de retenção, exclusão, minimização, mascaramento e finalidade para dados de proofing
- [X] T023 Executar `dotnet build`, `dotnet test`, `docker compose up` e smoke; corrigir falhas

## Dependencies

Setup → Foundational → US1 → US2 → US3 (US3 depende do engine/runner de US1 e das evidências de US2). Testes [P] antes da implementação.

## Implementation Strategy

MVP = US1 + US2 (sessão, evidências, resultado); US3 completa resiliência e proteção de dados.
