# Digital Signature Orchestrator

Plataforma de orquestração de assinaturas digitais (MVP), independente de fornecedor. Especificação em [`docs/PRD.md`](docs/PRD.md), princípios em [`.specify/memory/constitution.md`](.specify/memory/constitution.md), specs em [`specs/`](specs/), progresso em [`docs/PROGRESS.md`](docs/PROGRESS.md) e decisões em [`docs/DECISIONS.md`](docs/DECISIONS.md).

## Executar

Pré-requisitos: Docker (e .NET 8 SDK para rodar os testes).

```bash
docker compose up -d --build      # postgres, rabbitmq, api (:8080), worker
bash scripts/smoke-test.sh        # valida o fluxo ponta a ponta
docker compose down               # (-v apaga os dados)
```

- API: <http://localhost:8080> (Swagger em `/swagger`, health em `/health`)
- RabbitMQ management: <http://localhost:15672> (guest/guest)

## Testes

```bash
dotnet test tests/Orchestrator.UnitTests          # máquinas de estado, validação, provider simulado
dotnet test tests/Orchestrator.IntegrationTests   # PostgreSQL + RabbitMQ via Testcontainers (requer Docker)
```

## Uso rápido

```bash
curl -i -X POST localhost:8080/v1/signature-processes \
  -H "Idempotency-Key: minha-chave-1" -H "Content-Type: application/json" \
  -d @specs/001-core-orchestration/contracts/sample-request.json
curl localhost:8080/v1/signature-processes/{id}/status
curl localhost:8080/v1/signature-processes/{id}/operations
curl localhost:8080/v1/signature-processes/{id}/events
curl -X POST localhost:8080/v1/signature-processes/{id}/cancel
```

O provider é simulado: `externalId` com prefixo `SIM-REJECT-` faz o provider rejeitar a assinatura e `SIM-FAIL-` faz a criação falhar.

## Arquitetura (spec 001)

```
Consumidor ──► API ──(1 transação)──► PostgreSQL: processo + operação + journal + outbox
                                          │
                       Worker: Outbox Publisher ──► RabbitMQ (signature-provider, DLQ)
                       Worker: Operation Consumer ──► inbox ──► WorkflowEngine ──► IProviderAdapter (simulado)
```

- Estado de negócio e operacional são máquinas independentes (`Orchestrator.Domain/StateMachines`).
- Cada atividade é uma operação própria; o journal de eventos é append-only (trigger no banco).
- Mensagens carregam apenas referências; entrega at-least-once com inbox para idempotência.

## Estrutura

`src/Orchestrator.{Domain,Application,Infrastructure,Api,Worker}`, `tests/`, `deploy/` (Dockerfiles), `docker-compose.yml`, `scripts/`.

## Artefatos e download seguro (spec 002)

- Original, documento assinado e evidência ficam no MinIO (API S3), com SHA-256, tamanho e content type registrados em `artifact`.
- `GET /v1/signature-processes/{id}/artifacts` lista os artefatos; `POST /v1/signature-processes/{id}/download-link` (corpo opcional `{"type":"ORIGINAL_DOCUMENT"}` ou `{"artifactId":"..."}`) devolve `url` e `expiresAt` (5 min, HMAC-SHA256, um único artefato); `GET /v1/downloads/{artifactId}?expires=..&sig=..` entrega o conteúdo com `X-Content-SHA256`.
- Console do MinIO: <http://localhost:9001> (minioadmin / minioadmin123, somente desenvolvimento). O documento de exemplo vem do serviço `sample-docs`.

## Resiliência (spec 003)

- Falhas são classificadas em `TRANSIENT` (timeout, 408/429/5xx, rede), `PERMANENT` (4xx, payload/documento inválidos) e `UNKNOWN` (limite padrão de 3 tentativas). Transitórias são reagendadas com backoff e jitter (±20%) pelo cronograma do PRD (0, 5 s, 30 s, 2 min, 10 min, 30 min, 2 h, 6 h; 8 tentativas), configurável por tipo em `Retry:*` (ver `specs/003-resilience/data-model.md`). O compose usa um cronograma curto só para demonstração.
- Tentativas esgotadas → operação e processo em `DLQ`; a entrada aparece em `GET /v1/dead-letters?domain=&resolved=` e uma mensagem só de referências vai para a fila `<domínio>-dlq` (`signature-provider`, `artifact`, `identity-proofing`, `callback`).
- `POST /v1/signature-processes/{id}/retry` (corpo opcional `{"operationId":"op_...","reason":"..."}`) retoma a partir da operação que falhou, sem repetir as concluídas, e audita `OPERATION_REPROCESS_REQUESTED`.
- Falhas simuladas: `externalId` com `SIM-FLAKY-<n>-` (falha transitória nas n primeiras tentativas), `SIM-DOWN-` (indisponível), `SIM-UNKNOWN-` (erro inesperado), `SIM-FAIL-` (permanente) e `SIM-REJECT-` (rejeição).

## Callbacks (spec 004)

- Eventos `SIGNATURE_PROCESS.<STATUS>` (SIGNATURE_IN_PROGRESS, PARTIALLY_SIGNED, SIGNED, COMPLETED, REJECTED, CANCELLED, FAILED, EXPIRED) são enviados de forma assíncrona (operação `CALLBACK_SEND`, fila `callback`) ao destino informado em `callback` na criação do processo: `{"callbackId":"..."}` (preferencial) ou `{"url":"https://..."}`.
- Cada requisição leva `X-Signature-Event-Id`, `X-Signature-Timestamp` e `X-Signature-Signature` = `sha256=` + HMAC-SHA256(segredo, `"{timestamp}.{corpo}"`). O receptor deve recalcular e rejeitar timestamps fora de ~5 minutos (replay). `eventId` é estável entre tentativas (deduplique por ele). Veja `CallbackSigner.Verify` e `tools/CallbackSink`.
- Registro: `POST /v1/callbacks` (`callbackId`, `url`, `secret` opcional — o segredo só é mostrado na criação), `GET /v1/callbacks[/{id}]`, `DELETE /v1/callbacks/{id}` (desativa). Entregas: `GET /v1/signature-processes/{id}/callbacks`.
- Falha do receptor nunca muda o estado do processo; o envio usa retry/DLQ (`callback-dlq`) e pode ser reenviado com `POST /v1/signature-processes/{id}/retry` + `operationId` mesmo com o processo concluído.
- Proteção contra SSRF (padrão seguro): HTTPS obrigatório; bloqueio de localhost, IPs privados, loopback, link-local/metadata (169.254.169.254), CGNAT e IPv6 equivalentes; o DNS é resolvido uma vez e a conexão vai ao IP validado (anti DNS rebinding); sem redirects; timeout de 10 s; limite de 10 req/s por destino; allowlist opcional (`Callbacks:AllowedHosts`). O compose de desenvolvimento libera HTTP e rede privada (`Callbacks__AllowHttp`, `Callbacks__AllowPrivateNetworks`) só para falar com o receptor de exemplo em <http://localhost:8081/events>.

## Identity Proofing (spec 005)

Contexto separado da assinatura, usável sem nenhum processo de assinatura. Política de dados em [`docs/DATA-PROTECTION.md`](docs/DATA-PROTECTION.md).

- `POST /v1/proofing-sessions` (`Idempotency-Key` obrigatório): `externalId`, `subject` (`name`, `document` CPF, `phone`/`email`/`deviceId` conforme a capability) e `validations` (strings ou `{type, required}`) entre `PERSON_DATA`, `DOCUMENT_DATA`, `DOCUMENT_AUTHENTICITY`, `DOCUMENT_OWNERSHIP`, `FACE_MATCH`, `LIVENESS`, `GOVERNMENT_BIOMETRIC_MATCH`, `PHONE_OWNERSHIP`, `EMAIL_OWNERSHIP`, `DEVICE_RISK`, `IDENTITY_RISK`.
- Evidências: `POST /v1/proofing-sessions/{id}/documents` (`DOCUMENT_FRONT`/`DOCUMENT_BACK`) e `/biometrics` (`SELFIE`) com `{type, contentType: image/jpeg|image/png, content: base64}`; cada validação executa quando seus pré-requisitos chegam; `IDENTITY_RISK` agrega as demais ao final.
- Consulta: `GET /v1/proofing-sessions/{id}`, `/result` (agregado `PENDING|APPROVED|REJECTED` e por validação com score), `/events`, `/operations`. Reprocessar: `POST /v1/proofing-sessions/{id}/retry`. Excluir evidências (sessão concluída): `DELETE /v1/proofing-sessions/{id}/evidence`; retenção automática por `Identity:EvidenceRetentionDays`.
- Provider simulado: marcadores no conteúdo da evidência (`FAKE_SPOOF`, `FAKE_FACE_MISMATCH`, `FAKE_FORGED`, `FAKE_NOT_OWNER`, `FAKE_NO_MATCH`), CPF/telefone terminados em `0000`, e-mail com `fraud`, `deviceId` com `risky-`; falhas por prefixo do `externalId` (`SIM-FLAKY-n-`, `SIM-DOWN-`, `SIM-UNKNOWN-`, `SIM-FAIL-`).

## Reconciliação (spec 006)

- O Worker executa um reconciliador periódico (`Reconciliation:IntervalSeconds` 60, `StaleAfterSeconds` 120, `BatchSize` 50): processos em `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED` com registro no provider e sem atividade/conferência recente têm o estado comparado ao do provider (webhook + reconciliação).
- Divergência (provider assinado/parcial/rejeitado/cancelado) é corrigida pela máquina de estados, gera callbacks, cria `SIGNED_DOCUMENT_DOWNLOAD` (uma vez), cancela o acompanhamento obsoleto (resolvendo dead letters) e é auditada (`RECONCILIATION_DISCREPANCY_FOUND`/`RECONCILIATION_CORRECTED` + `reconciliation_record`). Sem divergência nada muda além de `last_reconciled_at`.
- Manual: `POST /v1/signature-processes/{id}/reconcile` (`CORRECTED`, `CONSISTENT`, `NOT_APPLICABLE`, `PROVIDER_ERROR`; 409 para processo terminal) e histórico em `GET /v1/signature-processes/{id}/reconciliations`.

## Portal operacional (spec 007)

Após `docker compose up -d --build`, abra http://localhost:3000.

- Lista de processos com filtros, busca e paginação; detalhe com timeline e abas (Overview, Signers, Identity Proofing, Operations, Artifacts, Provider Metadata, Audit Trail, Callbacks, Errors); página de dead letters.
- Operações manuais (Retry Operation, Retry Callback, Reprocess From Operation, Reconcile Provider, Download Artifact, Cancel Process, Inspect Provider Metadata) exigem um operator id (campo no topo, enviado como `X-Operator-Id`) e são gravadas no journal com o ator.
- Desenvolvimento: `cd portal && npm install && npm run dev` (proxy para http://localhost:8080); testes: `npm test`.

## Segurança e observabilidade (spec 008)

### Autenticação e papéis

No docker compose a API exige token OIDC (Keycloak, realm `orchestrator`, http://localhost:8180). Para desligar (desenvolvimento): `Auth__Enabled=false` (o ator volta a ser o cabeçalho `X-Operator-Id`).

| Identidade | Credencial de demonstração | Papel | Pode |
|---|---|---|---|
| `viewer` | senha `viewer` (client `orchestrator-cli` em scripts; portal por authorization code + PKCE) | viewer | leituras |
| `operator` | senha `operator` | operator | leituras e operações manuais |
| `admin` | senha `admin` | admin + operator | tudo |
| `orchestrator-client-a` / `-b` | client credentials, segredos em `deploy/keycloak/realm-orchestrator.json` | client | criar processos e ler/operar apenas os seus |

Os segredos e senhas acima são só de demonstração (não usar em produção; em produção use um Keycloak próprio, TLS e um secrets manager).

```bash
TOKEN=$(curl -s -X POST http://localhost:8180/realms/orchestrator/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=orchestrator-client-a -d client_secret=demo-secret-client-a-change-me \
  | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')
curl -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: demo-1" -H "Content-Type: application/json" \
  --data @specs/001-core-orchestration/contracts/sample-request.json http://localhost:8080/v1/signature-processes
```

O portal (http://localhost:3000) mostra o formulário de login quando a API responde 401. Ações manuais exigem papel `operator` ou `admin`. O ator gravado no journal é o usuário do token.

Segregação por cliente: processos criados por um `client` gravam o `client_id` (claim `azp`); outro cliente recebe 404. A idempotência é por cliente. A criação tem limite por cliente (`RateLimit__CreatePerMinute`, padrão 600).

### Observabilidade

API e worker exportam traces, métricas e logs por OTLP para o OpenTelemetry Collector (`otel-collector`); as métricas ficam em http://localhost:8889/metrics (Prometheus). Traces e logs aparecem no log do coletor (`docker compose logs otel-collector`). Um único trace liga a requisição de criação às operações do worker (`traceparent` viaja no outbox e no RabbitMQ); os spans carregam `correlation.id`, `process.id`, `operation.id`, `operation.type` e `provider.id`, e os logs levam `TraceId`, `CorrelationId`, `ProcessId` e `OperationId`.

Métricas: `orchestrator.process.created|completed|failed`, `orchestrator.process.completion_time`, `orchestrator.provider.latency|errors`, `orchestrator.operation.retries`, `orchestrator.deadletter.size`, `orchestrator.callback.deliveries`, `orchestrator.reconciliation.runs`, `orchestrator.proofing.failures`, `orchestrator.security.denied`.

Fora do MVP: WAF, KMS, secrets manager, circuit breaker/bulkhead, alertas (seção 41 do PRD).

## Endurecimento de segurança (spec 009)

- **Segregação por cliente** agora cobre processos, artefatos (via processo), sessões de identity proofing, callbacks registrados, dead letters e idempotência: clientes recebem 404 para recursos de outros clientes; `operator`, `viewer` e `admin` veem tudo. Callbacks registrados por `admin` são compartilhados para uso.
- **Portal**: login por authorization code com PKCE (S256) no client `portal` do Keycloak (sem password grant). Em desenvolvimento (`npm run dev`) o redirect é `http://localhost:5173/auth/callback`; `VITE_AUTH_URL` muda a origem do IdP.
- **Scripts**: para obter tokens de usuário por linha de comando use o client de desenvolvimento `orchestrator-cli` (`-d client_id=orchestrator-cli -d grant_type=password -d username=operator -d password=operator`).
- **Postgres** no host: `localhost:5433` (usuário/senha/banco `orchestrator`).

## Signatários, confirmação e progresso (spec 010)

**Criar com signatários simples** (`POST /v1/signature-processes`): cada signatário aceita `name`, `document` (CPF), `email`, `phone`, `signatureType` (`SIMPLE|ADVANCED|QUALIFIED`), `order` e `confirmation` (lista de canais `EMAIL|SMS|WHATSAPP`). `defaults.signatureType` e `defaults.confirmation` são herdados por quem não os informa (`confirmation: []` desliga a herança). Sem `order`, todos assinam em paralelo; com `order`, valores iguais assinam juntos e não pode haver lacunas. O payload anterior (`signature.type`, `externalId` do signatário) continua válido. Erros apontam `signers[i].campo`.

```json
{ "externalId": "CONTRATO-1",
  "document": { "fileName": "contrato.pdf", "source": { "type": "UPLOAD", "uploadId": "upl_..." } },
  "defaults": { "signatureType": "ADVANCED", "confirmation": ["EMAIL", "SMS"] },
  "signers": [ { "name": "Maria Souza", "document": "12345678909", "email": "maria@example.com", "phone": "+5511999990000", "order": 1 },
               { "name": "João Lima", "document": "98765432100", "email": "joao@example.com", "phone": "+5511988880000", "order": 2 } ] }
```

**Upload**: `POST /v1/document-uploads` (multipart, campo `file`) devolve `uploadId`, válido por 24 h e restrito ao cliente que enviou.

**Confirmação por canal**: antes de assinar, cada signatário recebe um código de 6 dígitos em cada canal exigido (quando chega a sua vez). `POST /v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/confirm` com `{"code":"123456"}` (200; 422 errado com `attemptsRemaining`; 410 expirado; 423 bloqueado) e `.../resend` (202; 429 com `Retry-After`). Configuração em `Confirmation` (validade 600 s, 5 tentativas, intervalo mínimo 30 s, 5 envios por canal). O código nunca é gravado em claro: só o HMAC no banco; o notificador simulado grava a mensagem em `notification_sink`, lida em desenvolvimento por `GET /v1/dev/confirmation-codes/{processId}` (admin, `Confirmation__ExposeSink=true`, ligado só no compose). Falhas simuladas: e-mail `sim-down@…`, `sim-flaky-N@…`, `sim-perm@…`.

**Progresso**: `progress { completedSteps, totalSteps, currentStep }` na listagem e no detalhe (com `steps`). Etapas: documento recebido, confirmação por signatário e canal, assinatura por signatário, documento final e, havendo callback, a entrega do callback de conclusão. Etapas canceladas ou com falha não contam.

**Portal**: coluna *Etapas* (X/Y, barra e etapa atual ao passar o mouse), aba *Etapas* no detalhe e página *Novo processo* (`/processes/new`, upload e tabela de signatários; disponível para `client`, `operator` e `admin`, nunca para `viewer`).

## Providers reais em sandbox (spec 011)

O provider simulado continua sendo o padrão e nada exige credenciais. Para usar **DocuSign** (demo) ou **Lacuna Signer** (demo):

1. `cp .env.example .env` (o `.env` é ignorado pelo git e pelo Docker) e preencha as variáveis descritas no próprio arquivo.
2. `Provider__Default=DocuSign` (ou `Lacuna`) no `.env`; `docker compose up -d --build` repassa as variáveis ao api e ao worker. Fora do Docker, API e Worker leem o `.env` automaticamente (variáveis do ambiente têm prioridade).
3. **Webhooks** (URL pública, por exemplo um túnel): DocuSign Connect (JSON, HMAC) para `POST /v1/webhooks/docusign`; Lacuna para `POST /v1/webhooks/lacuna` com `X-Webhook-Secret`. O webhook só dispara uma reconciliação: o estado vem sempre do provider.

Detalhes: a escolha vale para **novos** processos (os existentes seguem o provider em que nasceram); com confirmação por código, o documento só é enviado ao provider depois que todos confirmam; a ordem sequencial usa o roteamento do provider; e-mail é obrigatório por signatário. Documento assinado e evidências: PDF combinado e Certificate of Completion (DocuSign); conteúdo assinado e relatório de assinaturas (Lacuna).

**Testes**: a suíte padrão usa servidores falsos em processo (`FakeVendors`). Os testes `LiveProviderTests` rodam contra os sandboxes reais **somente** quando as credenciais existem no ambiente ou no `.env`; sem elas aparecem como *pulados*. Os mapeamentos HTTP foram escritos a partir da documentação pública e ainda precisam de uma primeira validação contra os sandboxes reais.

## Licença

Copyright 2026 Oscar Casagrande. Distribuído sob a [PolyForm Noncommercial License 1.0.0](LICENSE) (texto oficial, sem alterações; também em <https://polyformproject.org/licenses/noncommercial/1.0.0>).

- **Livre para estudo**: você pode ler, executar, modificar e redistribuir o código para fins **não comerciais**, como estudo, pesquisa, experimentos e projetos pessoais, e também uso por instituições de ensino, organizações sem fins lucrativos e órgãos públicos, nos termos da licença. Ao redistribuir, mantenha o arquivo `LICENSE` e a linha *Required Notice*.
- **Proibido para fins comerciais**: não é permitido usar o código, no todo ou em parte, em produtos, serviços ou operações com finalidade comercial, nem vendê-lo ou oferecê-lo como serviço, sem uma licença à parte do licenciante.
- Para uso comercial, entre em contato com o licenciante para negociar uma licença própria.

Este projeto é de código disponível (*source-available*); por restringir o uso comercial, **não** é open source no sentido da OSI. O texto da licença prevalece sobre este resumo.

Os componentes de terceiros (pacotes NuGet e npm, Keycloak, RabbitMQ, MinIO, PostgreSQL e demais imagens do `docker-compose.yml`) seguem as suas próprias licenças. Os segredos presentes no repositório (`docker-compose.yml`, realm do Keycloak, `appsettings`) são valores de demonstração; credenciais reais de sandbox ou produção ficam apenas no `.env`, que não é versionado.
