# Tasks: Callbacks Assinados e Seguros

**Input**: `/specs/004-callbacks/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: incluídos para fluxos críticos (assinatura, SSRF, entrega, retry independente), antes da implementação.

## Phase 1: Setup

- [X] T001 [P] Entidades `CallbackRegistration` (callback_id max 100 padrão `^[A-Za-z0-9_.-]+$`; url max 2000; secret max 200; description max 300) e `CallbackDelivery` (event_type max 80; process_status max 40; destination max 2100; status max 20; last_error max 500) e enum `DeliveryStatus` em `src/Orchestrator.Domain/Entities/Callbacks.cs`
- [X] T002 Mapeamento EF (`callback_registration`, `callback_delivery` com índices únicos em operation_id e event_id), `IOrchestratorDb.CallbackRegistrations/CallbackDeliveries` e migration `AddCallbacks` em `src/Orchestrator.Infrastructure/Persistence/`
- [X] T003 [P] `CallbackOptions` (DefaultSecret, AllowPrivateNetworks false, AllowHttp false, AllowedHosts, TimeoutSeconds 10, RateLimitPerSecond 10, SignatureToleranceSeconds 300) em `src/Orchestrator.Application/Callbacks/CallbackOptions.cs`

## Phase 2: Foundational

- [X] T004 [P] Testes unitários de `CallbackSigner` (formato `sha256=`, verificação válida, corpo/segredo/timestamp adulterados, replay fora da tolerância, comparação em tempo constante) em `tests/Orchestrator.UnitTests/CallbackSignerTests.cs`
- [X] T005 [P] Testes unitários de `SsrfGuard` (https obrigatório, localhost, IPs 10/8, 172.16/12, 192.168/16, 127/8, 169.254.169.254, 100.64/10, ::1, fc00::/7, fe80::/10, IPv4 mapeado, allowlist com curinga, AllowHttp/AllowPrivateNetworks) , do limitador por host e do handler protegido (ConnectCallback) bloqueando um servidor local real, tanto para callbacks quanto para `Artifacts:BlockPrivateNetworks` (FR-016), em `tests/Orchestrator.UnitTests/SsrfGuardTests.cs`
- [X] T006 `CallbackSigner` (Sign, Verify, headers) em `src/Orchestrator.Application/Callbacks/CallbackSigner.cs`
- [X] T007 `SsrfGuard` (ValidateUrl, IsBlocked(IPAddress), allowlist) e `HostRateLimiter` em `src/Orchestrator.Application/Callbacks/SsrfGuard.cs`
- [X] T008 `ICallbackSender` + `HttpCallbackSender` (SocketsHttpHandler.ConnectCallback resolvendo uma vez e validando IPs, sem redirects, timeout, rate limit, classificação da resposta) e handler compartilhado com `HttpDocumentFetcher` quando `Artifacts:BlockPrivateNetworks` em `src/Orchestrator.Infrastructure/Callbacks/HttpCallbackSender.cs` e `src/Orchestrator.Infrastructure/Storage/HttpDocumentFetcher.cs`

## Phase 3: User Story 3 — Registro e política de destino (P1)

**Independent Test**: registrar callback, criar processo por `callbackId`; URLs inválidas rejeitadas.

- [X] T009 [P] [US3] Testes de integração da API de registro (criação com segredo gerado/informado e exibido uma vez, GET sem segredo, duplicado 409, DELETE desativa, id inválido 400, URL bloqueada 400) e da criação de processo (callbackId inexistente/desativado, URL localhost/privada/http, ambos informados) em `tests/Orchestrator.IntegrationTests/CallbackRegistrationTests.cs`
- [X] T010 [US3] `CallbackAdmin` (registrar, listar, consultar, desativar) em `src/Orchestrator.Application/Callbacks/CallbackAdmin.cs` e endpoints `/v1/callbacks` em `src/Orchestrator.Api/Endpoints/CallbackEndpoints.cs`
- [X] T011 [US3] Validação do destino na criação do processo (`callback.callbackId` existente e ativo ou `callback.url` pela política; ambos → erro) em `src/Orchestrator.Application/Processes/CreateProcessHandler.cs`; remover a checagem fixa de https de `CreateProcessValidator` e ajustar o teste unitário correspondente

## Phase 4: User Story 1 — Eventos assinados (P1)

**Independent Test**: receptor Kestrel valida assinatura e recebe os eventos até `COMPLETED`.

- [X] T012 [P] [US1] Testes de integração com receptor Kestrel local (eventos SIGNATURE_IN_PROGRESS/SIGNED/COMPLETED, assinatura válida, corpo sem PII, `document.downloadUrl` baixável no COMPLETED, eventId/occurredAt estáveis, sem callback sem destino, callbackId usa o segredo do registro, destino dinâmico usa o segredo padrão) em `tests/Orchestrator.IntegrationTests/CallbackDeliveryTests.cs`
- [X] T013 [US1] `CallbackEmitter` (cria entrega + operação CALLBACK_SEND + journal `CALLBACK_REQUESTED` + outbox) em `src/Orchestrator.Application/Callbacks/CallbackEmitter.cs`
- [X] T014 [US1] `WorkflowEngine`: chamar o emitter nas transições SIGNATURE_IN_PROGRESS, PARTIALLY_SIGNED, SIGNED, COMPLETED, REJECTED e CANCELLED (provider); handler `CALLBACK_SEND` (payload, assinatura, envio, `CALLBACK_DELIVERED`, atualização da entrega) sem alterar o estado operacional do processo e executando com processo terminal, em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`
- [X] T015 [US1] `CancelProcessHandler` emite o evento CANCELLED e não cancela operações `CALLBACK_SEND` em `src/Orchestrator.Application/Processes/ProcessQueries.cs`
- [X] T016 [US1] Registrar serviços (Options, signer, guard, sender, emitter, admin, queries) e incluir a fila `callback` nas filas padrão do Worker em `src/Orchestrator.Infrastructure/DependencyInjection.cs` e `src/Orchestrator.Infrastructure/Messaging/RabbitMq.cs`

## Phase 5: User Story 2 — Retry independente e consulta (P1)

**Independent Test**: receptor falha 2×, entrega na 3ª, processo intacto; DLQ; reenvio manual com processo terminal.

- [X] T017 [P] [US2] Testes de integração (receptor 503 duas vezes → entregue sem alterar estado do processo; 4xx e 3xx permanentes; esgotamento → callback-dlq e dead letters; retry manual com processo COMPLETED; consulta de entregas; destino bloqueado por SSRF no envio é permanente) em `tests/Orchestrator.IntegrationTests/CallbackResilienceTests.cs`
- [X] T018 [US2] Atualizar o `WorkflowEngine` (falha/retry/DLQ de CALLBACK_SEND atualiza a entrega e não o processo) e o `ReprocessHandler` (CALLBACK_SEND permitido com processo terminal apenas com `operationId`, sem tocar o estado operacional; entrega volta a PENDING) em `src/Orchestrator.Application/Workflow/`
- [X] T019 [US2] `CallbackQueries` e endpoint `GET /v1/signature-processes/{id}/callbacks` em `src/Orchestrator.Application/Callbacks/CallbackQueries.cs` e `src/Orchestrator.Api/Endpoints/CallbackEndpoints.cs`

## Phase 6: Polish

- [X] T020 [P] Receptor de exemplo `tools/CallbackSink` (minimal API: valida assinatura, `GET /events`, falha controlável) com Dockerfile, serviço `callback-sink` no compose (porta 8081) e variáveis `Callbacks__AllowHttp`/`Callbacks__AllowPrivateNetworks` apenas para o compose de desenvolvimento
- [X] T021 [P] Atualizar `scripts/smoke-test.sh` (registro, callback assinado válido no sink, entrega consultável) e `README.md`
- [X] T022 Executar `dotnet build`, `dotnet test`, `docker compose up` e smoke; corrigir falhas

## Dependencies

Setup → Foundational → US3 (registro/política) → US1 (eventos) → US2 (resiliência/consulta). Testes [P] antes da implementação.

## Implementation Strategy

MVP = US3 + US1 (destino seguro e eventos assinados); US2 completa retry independente e consulta.
