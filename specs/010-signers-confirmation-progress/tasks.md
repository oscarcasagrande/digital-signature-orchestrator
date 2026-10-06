# Tasks: Signatários, confirmação e progresso

- [X] T001 [US1] Testes unitários do validador e do `SignerPlan` (tests/Orchestrator.UnitTests/SignerValidationTests.cs)
- [X] T002 [US1] Validador e `SignerPlan` em src/Orchestrator.Application/Processes
- [X] T003 [US1,US2] Entidades, DbContext e migration `AddSignerConfirmation`
- [X] T004 [US1] `CreateProcessHandler` grava signatários efetivos e confirmações PLANNED; fonte UPLOAD
- [X] T005 [US2] Testes unitários de `ConfirmationService` e do provider gated
- [X] T006 [US2] `IConfirmationNotifier`, notificador e sink simulados, `ConfirmationService`
- [X] T007 [US2] Operações `CONFIRMATION_SEND`/`CONFIRMATION_VERIFY`, domínio `notification`, `SignerAdvancer` no workflow, `ReleaseSignerAsync` no provider
- [X] T008 [US2] Endpoints confirm/resend, sink de desenvolvimento, RBAC
- [X] T009 [US3] Testes unitários do `ProgressCalculator`
- [X] T010 [US3] `ProgressCalculator`, `progress` na lista e no detalhe, mascaramento de contatos
- [X] T011 [US4] Upload de documento (API)
- [X] T012 Testes de integração (criação, confirmação, sequencial, retry, segredo, progresso, upload, segregação)
- [X] T013 [US4] Portal: coluna Etapas, detalhe, formulário e testes
- [X] T014 Swagger com exemplos, smoke test, README
- [X] T015 Rodar tudo, DECISIONS, PROGRESS, merge em main
