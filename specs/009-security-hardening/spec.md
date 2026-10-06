# Feature Specification: Endurecimento de segurança

**Feature Branch**: `009-security-hardening`

**Created**: 2026-10-06

**Status**: Implemented

**Input**: (1) segregar por cliente sessões de identity proofing, callbacks registrados e artefatos (404 a quem não é dono, com testes de acesso cruzado em todos os endpoints); (2) trocar o login por password grant do portal por authorization code com PKCE contra o Keycloak; (3) mudar a porta do Postgres no host para 5433.

## Clarifications

### Session 2026-10-06

- Q: Como os artefatos são segregados, já que não têm cliente próprio? → A: Artefatos pertencem a um processo; herdam o dono do processo. Listagem e `download-link` ficam sob `/v1/signature-processes/{id}` (404 para não donos) e o `artifactId` informado precisa pertencer ao processo da rota (404 caso contrário). A URL assinada de `/v1/downloads/{artifactId}` continua sendo capacidade temporária, emitida só ao dono. Evidências de proofing nunca são baixáveis.
- Q: Como são segregados sessões e callbacks? → A: Novas colunas `client_id` em `proofing_session` e `callback_registration`, preenchidas com o `azp` de chamadores `client`. Rotas `/v1/proofing-sessions/{id}/**` e `/v1/callbacks/{id}` devolvem 404 a clientes não donos; a listagem de callbacks e a de dead letters mostram só os recursos do cliente. `operator`, `viewer` e `admin` veem tudo.
- Q: O que acontece com recursos sem dono (`client_id` nulo) e com callbacks criados por admin? → A: Registros sem dono (admin ou dados anteriores à spec) ficam invisíveis para clientes, mas callbacks sem dono continuam utilizáveis em processos de qualquer cliente (compartilhados). Um callback de outro cliente não pode ser usado: a criação do processo falha com o mesmo erro de callback desconhecido (sem revelar existência).
- Q: Idempotência de sessões de proofing? → A: Chave por cliente, como nos processos.
- Q: Como é o fluxo do portal? → A: Authorization code com PKCE S256 (client público `portal`, `standardFlowEnabled`, `directAccessGrantsEnabled=false`, PKCE obrigatório no Keycloak, redirect URIs exatas `/auth/callback`). O portal gera verifier e state aleatórios (uso único em `sessionStorage`), redireciona ao Keycloak (origem do IdP, `VITE_AUTH_URL` ou `<host>:8180`), recebe o código na rota `/auth/callback`, valida o state e troca o código pelo token no endpoint de token servido pelo proxy do nginx (mesma origem). Sem refresh token: token expirado volta ao login.
- Q: E scripts e smoke test que usavam password grant? → A: Um client separado `orchestrator-cli` (público, password grant, só desenvolvimento) atende scripts e o smoke; o client `portal` não aceita mais password grant.
- Q: Porta do Postgres? → A: Host `5433` (container segue em 5432); a rede interna do compose não muda; defaults de `appsettings` de desenvolvimento passam a `Port=5433`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Isolamento de sessões, callbacks e artefatos (Priority: P1)

Um cliente nunca acessa, nem descobre, recursos de outro cliente.

**Independent Test**: Varredura de todas as rotas com id de `/v1/signature-processes`, `/v1/proofing-sessions` e `/v1/callbacks` com o token de outro cliente: todas respondem 404; o dono e operadores continuam com acesso.

**Acceptance Scenarios**:
1. **Given** sessão do cliente A, **When** B chama qualquer rota da sessão (GET, upload de evidência, retry, remoção de evidência), **Then** 404.
2. **Given** callback do cliente A, **When** B consulta, desativa ou o usa em um processo, **Then** 404 (consulta e desativação) e erro de callback desconhecido (uso); A continua com o callback ativo.
3. **Given** artefato do processo de A, **When** B pede link de download pelo processo de A ou usa o artifactId de A no seu processo, **Then** 404.
4. **Given** listagens de callbacks e dead letters, **Then** clientes veem só os seus; operador e admin veem todos.
5. **Given** a mesma Idempotency-Key em dois clientes, **Then** sessões distintas.

### User Story 2 - Login do portal com authorization code e PKCE (Priority: P1)

**Independent Test**: Testes do portal cobrem o RFC 7636, a requisição de autorização, a troca do código com o verifier, state inválido, callback sem login pendente e erro do IdP; o smoke confirma que o client `portal` recusa password grant e exige PKCE.

### User Story 3 - Postgres na porta 5433 do host (Priority: P3)

**Independent Test**: `docker compose` publica 5433; o smoke conecta em `localhost:5433`.

## Requirements

- **FR-001**: `proofing_session` e `callback_registration` DEVEM ter `client_id` (nulo permitido), preenchido pelo `azp` de chamadores `client`.
- **FR-002**: Rotas `/v1/proofing-sessions/{id}/**` e `/v1/callbacks/{id}` DEVEM responder 404 a clientes não donos, antes de qualquer efeito.
- **FR-003**: Listagem de callbacks e de dead letters DEVE filtrar por cliente para chamadores `client`.
- **FR-004**: Callback de outro cliente NÃO PODE ser usado na criação de processo; callbacks sem dono são compartilhados.
- **FR-005**: Idempotência de sessões de proofing DEVE ser por cliente.
- **FR-006**: Download de artefato DEVE exigir que o `artifactId` pertença ao processo da rota; a URL assinada permanece capacidade temporária.
- **FR-007**: O portal DEVE autenticar por authorization code com PKCE S256, validar `state`, usar verifier e state de uso único e não usar password grant.
- **FR-008**: O Keycloak DEVE exigir PKCE no client `portal`, recusar password grant nele e restringir redirect URIs.
- **FR-009**: O compose DEVE publicar o Postgres na porta 5433 do host.
- **FR-010**: Testes de integração DEVEM cobrir acesso cruzado em todos os endpoints dos três recursos.

## Success Criteria

- **SC-001**: A varredura automática cobre mais de 20 endpoints e todos devolvem 404 ao cliente não dono.
- **SC-002**: O portal conclui o login apenas com state válido e verifier correspondente.
- **SC-003**: Suíte completa e smoke test passam.

## Out of Scope

Refresh token e logout federado do Keycloak, segregação de dead letters resolvidas por tela, rotação de segredos.
