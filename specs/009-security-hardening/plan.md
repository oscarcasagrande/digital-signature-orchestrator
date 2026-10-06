# Implementation Plan: Endurecimento de segurança

**Spec**: [spec.md](spec.md)

## Design

- **Dados**: migration `AddClientSegregationProofingCallbacks` (`client_id` nulo em `proofing_session` e `callback_registration`).
- **API**: `AccessMiddleware` passa a verificar posse por prefixo de rota (`signature-processes`, `proofing-sessions`, `callbacks`) com regex única; 404 igual ao de recurso inexistente. `CallerContext` entra por parâmetro opcional em `ProofingService`, `CallbackAdmin`, `CreateProcessHandler` e `DeadLetterQueries`.
- **Callbacks**: `CallbackAdmin` filtra por cliente (listar, obter, desativar) e grava o dono; a criação de processo rejeita callback de outro dono com a mesma mensagem de desconhecido.
- **Proofing**: chave de idempotência inclui o cliente; sessão grava `ClientId`; dead letters de sessões entram no filtro.
- **Artefatos**: sem mudança de modelo; a verificação por processo já existe (testes de acesso cruzado novos a comprovam).
- **Portal**: `api/auth.ts` com PKCE (verifier/state aleatórios, S256 via WebCrypto), `pages/Login` redireciona, `pages/AuthCallback` troca o código; `redirectTo` em `util/navigation`; `OperatorContext.adopt`.
- **Keycloak**: client `portal` com `standardFlowEnabled`, PKCE S256 obrigatório, redirect URIs exatas, sem password grant; client `orchestrator-cli` para scripts/smoke.
- **Compose**: Postgres `5433:5432`; `appsettings` de desenvolvimento com 5433.

## Testes

- `SegregationTests` (integração): sessões, callbacks, artefatos, idempotência de sessões, callbacks compartilhados e varredura de todas as rotas com id.
- Portal `Auth.test.tsx`: RFC 7636, requisição de autorização, troca com verifier, state inválido, replay, erro do IdP, falha do token endpoint.
- Smoke: PKCE obrigatório, password grant recusado no `portal`, segregação de sessões e callbacks, porta 5433.
