# Tasks: Portal Operacional

**Input**: `/specs/007-operations-portal/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: testes de integração do backend e Vitest/Testing Library do portal, antes da implementação dos fluxos críticos.

## Phase 1: Setup (backend e portal)

- [X] T001 [P] Coluna `DocumentFileName` (max 300) em `SignatureProcess`, preenchida em `CreateProcessHandler`, mapeamento EF e migration `AddPortalSupport` com retroalimentação `UPDATE signature_process SET document_file_name = request_json->'document'->>'fileName'` em `src/Orchestrator.Domain/Entities/SignatureProcess.cs`, `src/Orchestrator.Application/Processes/CreateProcessHandler.cs` e `src/Orchestrator.Infrastructure/Persistence/`
- [X] T002 [P] Criar o projeto `portal/` (Vite + React 18 + TypeScript, react-router-dom, vitest, jsdom, Testing Library) com `package.json`, `vite.config.ts` (proxy `/v1` e configuração de testes), `tsconfig.json`, `index.html`, `.gitignore` e `.dockerignore`
- [X] T003 [P] `SlaOptions` (`Sla:SigningMinutes` padrão 60) e `ActorContext` (Type, Id; padrão CONSUMER/api) em `src/Orchestrator.Application/Abstractions/` e `src/Orchestrator.Application/Processes/`

## Phase 2: Foundational (backend)

- [X] T004 [P] Testes de integração do ator (`X-Operator-Id` válido vira ator OPERATOR em cancelar, reprocessar, reconciliar, link de download, criação; inválido → 400; ausente → CONSUMER/api) em `tests/Orchestrator.IntegrationTests/ActorTests.cs`
- [X] T005 `ActorMiddleware` (valida `^[A-Za-z0-9._@:-]{1,100}$`, preenche `ActorContext`) registrado antes dos endpoints e DI scoped em `src/Orchestrator.Api/Middleware.cs`, `src/Orchestrator.Api/Program.cs` e `src/Orchestrator.Infrastructure/DependencyInjection.cs`
- [X] T006 Handlers disparados pela API passam a usar `ActorContext` (CreateProcess, Cancel, Reprocess, ReprocessIdentity, Reconciliation manual, DownloadService, EvidencePurger manual, Proofing create/evidence) em `src/Orchestrator.Application/**`

## Phase 3: User Story 1 — Lista de processos (P1)

**Independent Test**: criar processos em estados distintos e validar colunas, filtros, busca, paginação e SLA.

- [X] T007 [P] [US1] Testes de integração da lista (colunas, provider nulo/presente, assinantes, filtros por status e operacional, busca por id e externalId sem distinção de maiúsculas, paginação sem repetição, ordenação, SLA OK/ALERT por estado e por idade, filtro inválido 400, detalhe com documento/provider/sla) em `tests/Orchestrator.IntegrationTests/PortalApiTests.cs`
- [X] T008 [P] [US1] Testes Vitest da página de lista (renderiza linhas e colunas, SLA, filtros e busca chamam a API com os parâmetros, paginação, estados vazio/erro/carregando com nova tentativa, atualização automática) em `portal/src/__tests__/ProcessList.test.tsx`
- [X] T009 [US1] `ProcessQueries.ListAsync` (filtros, subconsultas de assinantes/provider, SLA), `ProcessDto` estendido e endpoint `GET /v1/signature-processes` em `src/Orchestrator.Application/Processes/ProcessQueries.cs` e `src/Orchestrator.Api/Endpoints/SignatureProcessEndpoints.cs`
- [X] T010 [US1] Cliente HTTP (`api/client.ts` com `X-Operator-Id`, `ApiError` a partir de problem+json), tipos, `useApi`, layout (`App.tsx`, navegação, `OperatorBar`), `StatusBadge`, estilos e página `ProcessList` em `portal/src/`

## Phase 4: User Story 2 — Detalhe, timeline e abas (P1)

**Independent Test**: abrir processo concluído e com falha e verificar cabeçalho, timeline e as 9 abas.

- [X] T011 [P] [US2] Testes de integração de `GET .../provider` (metadados, 404 sem registro, evento `PROVIDER_METADATA_INSPECTED` com ator) e do filtro `processId` em dead letters em `tests/Orchestrator.IntegrationTests/PortalApiTests.cs`
- [X] T012 [P] [US2] Testes Vitest do detalhe (cabeçalho, timeline com rótulos legíveis, cada aba com seus dados, aba por `?tab=`, signatário mascarado, Errors reúne operações e dead letters, não encontrado, carregar mais eventos, abas e botões acessíveis por papel/rótulo e navegação por teclado entre abas, FR-015) em `portal/src/__tests__/ProcessDetail.test.tsx`
- [X] T013 [US2] `ProviderInfo` query + endpoint `GET /v1/signature-processes/{id}/provider` com auditoria e filtro `processId` em `/v1/dead-letters` em `src/Orchestrator.Application/Processes/ProcessQueries.cs`, `src/Orchestrator.Application/Resilience/Resilience.cs` e `src/Orchestrator.Api/Endpoints/`
- [X] T014 [US2] Página `ProcessDetail` com `Timeline`, `Tabs` e as abas Overview, Signers, Identity Proofing, Operations, Artifacts, Provider Metadata, Audit Trail, Callbacks e Errors, `JsonView` e estilos responsivos em `portal/src/pages/` e `portal/src/components/`

## Phase 5: User Story 3 — Operações manuais com auditoria (P1)

**Independent Test**: executar cada operação manual e verificar efeito, resultado e evento com o operador.

- [X] T015 [P] [US3] Testes de integração do evento `RECONCILIATION_REQUESTED` (inclusive resultado consistente) com ator OPERATOR e atualização dos testes da spec 006 em `tests/Orchestrator.IntegrationTests/PortalApiTests.cs` e `ReconciliationTests.cs`
- [X] T016 [P] [US3] Testes Vitest das operações manuais (confirmação e motivo, chamadas e cabeçalho `X-Operator-Id`, resultado exibido, erro 409 exibido, desabilitação por estado, exigência do operador com identificador limitado a 100 caracteres seguros, download por link, inspect de metadados) em `portal/src/__tests__/ManualOperations.test.tsx`
- [X] T017 [US3] `ReconciliationService` grava `RECONCILIATION_REQUESTED` em toda chamada manual em `src/Orchestrator.Application/Reconciliation/ReconciliationService.cs`
- [X] T018 [US3] `ConfirmDialog`, ações no detalhe (Cancel Process, Reconcile Provider, Retry/Reprocess Operation, Retry Callback, Download Artifact, Inspect Provider Metadata) e regras de habilitação em `portal/src/components/` e `portal/src/pages/ProcessDetail.tsx`

## Phase 6: User Story 4 — Dead letters (P2)

- [X] T019 [P] [US4] Testes Vitest da página de dead letters (colunas, filtros de domínio e pendentes/resolvidas, paginação, link ao processo na aba Errors) em `portal/src/__tests__/DeadLetters.test.tsx`
- [X] T020 [US4] Página `DeadLetters` em `portal/src/pages/DeadLetters.tsx`

## Phase 7: Polish

- [X] T021 [P] `portal/nginx.conf` (SPA com `try_files`, proxy `/v1` → `api:8080`), `portal/Dockerfile` (node build → nginx), serviço `portal` (porta 3000) e `Artifacts__PublicBaseUrl` apontando ao portal no `docker-compose.yml`
- [X] T022 [P] Atualizar `scripts/smoke-test.sh` (portal servido, SPA fallback, proxy `/v1`, lista de processos, auditoria do operador) e `README.md`
- [X] T023 Executar `dotnet build`, `dotnet test`, `npm test`, `npm run build`, `docker compose up`, smoke e verificação visual no navegador; corrigir falhas

## Dependencies

Setup → Foundational → US1 → US2 → US3 → US4 → Polish (US3/US4 dependem do cliente e das páginas de US1/US2). Testes [P] antes da implementação.

## Implementation Strategy

MVP = US1 + US2 (observabilidade); US3 adiciona ações auditadas; US4 completa a visibilidade de DLQ.
