# Tasks: Endurecimento de segurança

- [X] T001 [US1] Testes de integração de acesso cruzado em tests/Orchestrator.IntegrationTests/SegregationTests.cs
- [X] T002 [US1] Colunas `client_id` em ProofingSession e CallbackRegistration + migration `AddClientSegregationProofingCallbacks`
- [X] T003 [US1] Verificação de posse por prefixo no `AccessMiddleware` (src/Orchestrator.Api/Security.cs)
- [X] T004 [US1] Escopo por cliente em `ProofingService`, `CallbackAdmin`, `CreateProcessHandler` e `DeadLetterQueries`
- [X] T005 [US2] PKCE em portal/src/api/auth.ts, páginas Login e AuthCallback, rota `/auth/callback`
- [X] T006 [US2] Testes Vitest em portal/src/__tests__/Auth.test.tsx
- [X] T007 [US2] Realm Keycloak: client `portal` (code + PKCE) e `orchestrator-cli`
- [X] T008 [US3] Postgres 5433 no docker-compose.yml e appsettings de desenvolvimento
- [X] T009 Smoke test, README, DECISIONS e PROGRESS
