# Feature Specification: Segurança (OIDC/RBAC) e Observabilidade (OpenTelemetry)

**Feature Branch**: `008-security-observability`

**Created**: 2026-10-06

**Status**: Draft

**Input**: PRD seções 37, 39 e 40: autenticação OIDC com Keycloak (usuários e machine-to-machine), RBAC, segregação por cliente, auditoria com a identidade autenticada, e observabilidade com OpenTelemetry (logs, métricas e traces correlacionados por correlationId, processId, operationId e providerId).

## Clarifications

### Session 2026-10-06

- Q: A autenticação é obrigatória sempre? → A: Configurável por `Auth:Enabled`. Desligada, a API mantém o comportamento da spec 007 (ator por `X-Operator-Id`, sem restrição), para desenvolvimento e testes antigos. Ligada (padrão no docker compose), todo `/v1/*` exige token Bearer válido; `/health` e `/swagger` ficam públicos.
- Q: Quais papéis existem? → A: `viewer` (leituras), `operator` (leituras e operações manuais: cancel, reconcile, retry/reprocess, download-link, provider), `client` (conta de serviço: cria processos e lê/opera somente os seus) e `admin` (tudo, inclusive registrar callbacks e ver todos os clientes). Os papéis vêm do claim `realm_access.roles` do Keycloak.
- Q: Como é a segregação por cliente? → A: O claim `azp` (client id do token M2M) identifica o cliente. Processos criados por um `client` gravam `client_id`; o `client` só enxerga e opera os processos do seu `client_id` (outros devolvem 404). `viewer`, `operator` e `admin` veem todos. A idempotência também é segregada por `client_id` quando o chamador é `client`.
- Q: Como fica o ator de auditoria com autenticação? → A: Com token válido, o ator é `OPERATOR` com id igual ao claim `preferred_username` (ou `CONSUMER` com o `azp` para M2M); `X-Operator-Id` é ignorado. O portal passa a enviar o token.
- Q: Como o portal se autentica? → A: Login por formulário simples do portal (client público `portal`, password grant do realm de demonstração), token em sessionStorage enviado como Bearer; sem fluxo de redirecionamento, por simplicidade (decisão registrada).
- Q: Quais sinais de observabilidade e para onde vão? → A: Traces, métricas e logs via OTLP para um OpenTelemetry Collector no compose, que expõe as métricas em formato Prometheus (porta 8889) e imprime traces e logs (debug exporter). Serviços `orchestrator-api` e `orchestrator-worker`.
- Q: Como a correlação funciona? → A: Cada span de operação carrega `correlation.id`, `process.id`, `operation.id`, `operation.type` e `provider.id`; logs levam `TraceId`, `SpanId`, `CorrelationId`; o contexto de trace atravessa o outbox e o RabbitMQ (header `traceparent`) para que a requisição e as operações do worker formem um único trace.
- Q: Rate limiting, WAF, KMS? → A: Fora do MVP, exceto um limitador simples por cliente na criação de processos (`RateLimit:CreatePerMinute`, padrão 600). WAF, KMS e secrets manager ficam como infraestrutura externa, documentados.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Acesso autenticado com papéis (Priority: P1)

Operadores e sistemas clientes só acessam a API com token OIDC válido, e cada papel só executa o que lhe cabe.

**Independent Test**: Com `Auth:Enabled=true`, chamar sem token (401), com token de `viewer` tentando cancelar (403), com `operator` cancelando (200) e com `client` criando processo (201).

**Acceptance Scenarios**:

1. **Given** autenticação ligada, **When** a requisição não traz token ou traz token inválido/expirado/de outro emissor, **Then** 401 com problem+json.
2. **Given** `viewer`, **When** executa uma operação manual, **Then** 403.
3. **Given** `operator`, **When** executa operações manuais e leituras, **Then** sucesso; criar processo exige `client` ou `admin`.
4. **Given** token válido, **When** uma operação manual é feita, **Then** o journal registra o ator com a identidade do token e `X-Operator-Id` é ignorado.
5. **Given** `/health`, **Then** continua público.

### User Story 2 - Segregação por cliente (Priority: P1)

Cada cliente máquina-a-máquina só vê os próprios processos.

**Independent Test**: Dois clientes criam processos; cada um lista e consulta só os seus (404 para o outro); `operator` vê ambos.

**Acceptance Scenarios**:

1. **Given** processos dos clientes A e B, **When** A lista, **Then** só os de A; **When** A consulta um de B, **Then** 404.
2. **Given** a mesma Idempotency-Key usada por A e B, **Then** não colidem.
3. **Given** `operator` ou `admin`, **Then** veem processos de todos os clientes.

### User Story 3 - Portal com login (Priority: P2)

O portal exige login, mostra o usuário e desabilita ações sem permissão.

**Independent Test**: Teste de componente: sem sessão aparece o login; com `viewer` as ações manuais ficam desabilitadas; com `operator` habilitadas; 401 da API volta ao login.

### User Story 4 - Rastreabilidade ponta a ponta (Priority: P1)

Dado um processo, é possível seguir uma única trace da requisição de criação até as operações do worker, com logs correlacionados.

**Independent Test**: Teste de integração com `ActivityListener`: a criação gera o span da API e spans de operação no consumidor com o mesmo `TraceId`, contendo `process.id`, `operation.id`, `provider.id` e `correlation.id`.

**Acceptance Scenarios**:

1. **Given** criação de processo, **Then** os spans das operações executadas pelo worker pertencem ao mesmo trace da requisição.
2. **Given** um span de operação, **Then** carrega `correlation.id`, `process.id`, `operation.id`, `operation.type` e `provider.id`.
3. **Given** logs da aplicação, **Then** incluem `TraceId` e `CorrelationId`.

### User Story 5 - Métricas de negócio e operação (Priority: P1)

As métricas mínimas da seção 40 são emitidas e consultáveis no coletor.

**Independent Test**: Teste de integração com `MeterListener` verifica os instrumentos após um fluxo completo; o smoke test lê `/metrics` do coletor.

**Acceptance Scenarios**:

1. **Given** um fluxo completo, **Then** existem: `orchestrator.process.created`, `orchestrator.process.completed`, `orchestrator.process.failed`, `orchestrator.process.completion_time` (histograma), `orchestrator.provider.latency` (histograma), `orchestrator.provider.errors`, `orchestrator.operation.retries`, `orchestrator.deadletter.size` (gauge de pendentes), `orchestrator.callback.deliveries` (por resultado), `orchestrator.reconciliation.runs` (por resultado), `orchestrator.proofing.failures`; a conversão de assinatura é derivável de completed/created.

## Requirements *(mandatory)*

- **FR-001**: Com `Auth:Enabled=true`, toda rota `/v1/*` DEVE exigir JWT Bearer validado (emissor, audiência, assinatura, expiração); `/health` e `/swagger` são públicos.
- **FR-002**: Papéis `viewer`, `operator`, `client`, `admin` DEVEM ser lidos de `realm_access.roles` e aplicados por política em cada endpoint conforme a matriz da spec.
- **FR-003**: `client_id` DEVE ser gravado no processo (coluna nova, migration) e usado para segregar listagem, consulta, operações e idempotência de chamadores `client`; acesso a recurso de outro cliente DEVE devolver 404.
- **FR-004**: O ator de auditoria DEVE ser derivado do token quando a autenticação estiver ligada; `X-Operator-Id` só vale com autenticação desligada.
- **FR-005**: Falhas de autenticação/autorização DEVEM ser registradas (log e métrica `orchestrator.security.denied`) sem vazar dados sensíveis.
- **FR-006**: O compose DEVE incluir Keycloak com realm importado (client público `portal`, M2M `orchestrator-client-a` e `orchestrator-client-b` com segredo de demonstração, usuários `viewer`, `operator`, `admin` com senha de demonstração) e o OpenTelemetry Collector.
- **FR-007**: API e worker DEVEM exportar traces, métricas e logs via OTLP quando `OTEL_EXPORTER_OTLP_ENDPOINT` estiver definido, com `service.name` distinto.
- **FR-008**: Cada execução de operação DEVE gerar um span com atributos `correlation.id`, `process.id`, `operation.id`, `operation.type`, `provider.id`; o contexto W3C `traceparent` DEVE ser propagado pelo outbox até o consumidor.
- **FR-009**: Os instrumentos de métrica listados no cenário 5 DEVEM existir com nome estável.
- **FR-010**: Logs DEVEM conter `TraceId`, `SpanId`, `CorrelationId`; dados sensíveis continuam mascarados.
- **FR-011**: O portal DEVE oferecer login, envio do Bearer, exibição do usuário, desabilitar ações sem papel `operator`/`admin` e voltar ao login em 401.
- **FR-012**: Um limitador simples por cliente DEVE proteger a criação de processos (429 ao exceder).
- **FR-013**: Segredos de demonstração DEVEM ficar apenas em arquivos de desenvolvimento (compose/realm) e ser documentados como não utilizáveis em produção.

## Success Criteria

- **SC-001**: 100% das rotas `/v1/*` rejeitam requisições sem token quando a autenticação está ligada (teste que percorre as rotas).
- **SC-002**: Nenhum cliente acessa processo de outro (teste cruzado).
- **SC-003**: Um processo criado produz um único trace com spans da API e do worker.
- **SC-004**: Todas as métricas mínimas aparecem no endpoint Prometheus do coletor após o smoke test.

## Assumptions

- Keycloak 25 em modo `start-dev` com realm importado é suficiente para o MVP.
- Testes de integração usam emissor JWT local com chave simétrica, sem Keycloak; o smoke test usa o Keycloak real.

## Out of Scope

WAF, KMS, secrets manager, circuit breaker, alertas (seção 41), fluxo OIDC com redirecionamento no portal.
