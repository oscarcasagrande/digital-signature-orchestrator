# Arquitetura

Detalhe técnico da solução como ela existe hoje. O produto, os contratos e o estado de cada requisito estão no [PRD](PRD.md); este documento guarda o que não cabe em um PRD: projetos, modelo de dados, filas, fluxo de execução, workers, configuração e ambiente local.

Fontes: código em `src`, `portal`, `tools`; `docker-compose.yml` e `deploy/`; migrações do banco; `docs/DECISIONS.md`. Onde uma decisão antiga diverge do código, vale o código (marcado como nota).

Nenhum segredo aparece aqui. Credenciais de desenvolvimento existem no `docker-compose.yml`, no realm do Keycloak e nos padrões de algumas classes de opções; são só para uso local e devem ser substituídas em qualquer outro ambiente.

## Sumário

1. [Visão geral](#1-visão-geral)
2. [Projetos da solução](#2-projetos-da-solução)
3. [Modelo de dados](#3-modelo-de-dados)
4. [Filas, DLQs e mensagens](#4-filas-dlqs-e-mensagens)
5. [Outbox, inbox e execução de uma operação](#5-outbox-inbox-e-execução-de-uma-operação)
6. [Workers](#6-workers)
7. [Fluxo de um processo de assinatura](#7-fluxo-de-um-processo-de-assinatura)
8. [Armazenamento de objetos](#8-armazenamento-de-objetos)
9. [Configuração por variáveis de ambiente](#9-configuração-por-variáveis-de-ambiente)
10. [Serviços do docker-compose e portas](#10-serviços-do-docker-compose-e-portas)
11. [Identidade, coletor OpenTelemetry e proxy do portal](#11-identidade-coletor-opentelemetry-e-proxy-do-portal)
12. [Observabilidade](#12-observabilidade)
13. [Segurança de infraestrutura](#13-segurança-de-infraestrutura)
14. [Pontos de atenção](#14-pontos-de-atenção)
15. [Como executar e testar](#15-como-executar-e-testar)

---

## 1. Visão geral

```text
 Consumidores / pessoas
        │                           ┌──────────────┐
        │  HTTP                     │ Portal       │  nginx :3000
        ▼                           │ (React/Vite) │──── /v1 ────┐
 ┌──────────────┐                   └──────┬───────┘             │
 │ Orchestrator │◄─────────────────────────┘ /realms             │
 │ .Api  :8080  │                                                │
 └──────┬───────┘        ┌───────────┐                           │
        │ transação       │ Keycloak  │◄── OIDC / JWT ───────────┘
        ▼                 │   :8180   │
 ┌──────────────┐        └───────────┘
 │ PostgreSQL   │◄────────────────────────────┐
 │ + outbox     │                             │
 └──────┬───────┘                             │
        │ SELECT ... SKIP LOCKED              │
        ▼                                     │
 ┌──────────────────────────────────┐         │
 │ Orchestrator.Worker              │─────────┘
 │  OutboxPublisher ──► RabbitMQ    │
 │  5 consumidores (1 por fila)     │──► S3/MinIO, providers, callbacks
 │  ReconciliationWorker            │
 │  EvidenceRetentionService        │
 └──────────────────────────────────┘
```

- A **API** grava estado e mensagem de outbox na mesma transação e **não** fala com o RabbitMQ.
- O **Worker** publica o outbox, consome as filas, executa o motor de workflow e roda o reconciliador e a retenção de evidências.
- Os dois processos compartilham o mesmo núcleo (`AddOrchestratorCore`) e o mesmo banco.

## 2. Projetos da solução

Ferramental: .NET SDK 8.0.422 (`global.json`, `rollForward: latestFeature`); `net8.0`, `Nullable` e `ImplicitUsings` habilitados, `LangVersion 12` (`Directory.Build.props`). A solução `DigitalSignature.sln` tem 5 projetos em `src`, 2 em `tests` e 1 em `tools`; o portal não faz parte dela.

| Projeto | Tipo | Referencia | Responsabilidade |
|---|---|---|---|
| `src/Orchestrator.Domain` | biblioteca | — | Entidades, enums e máquinas de estado (`BusinessStateMachine`, `Statuses`). Sem pacotes. |
| `src/Orchestrator.Application` | biblioteca | Domain | Casos de uso e portas: `WorkflowEngine`, `CreateProcessHandler`, validadores, `SignerPlan`, `ProgressCalculator`, `RetryPolicy`, `ErrorClassifier`, `OperationDomains`, `EventRecorder`, callbacks (HMAC, `SsrfGuard`), `DownloadLinkSigner`, confirmação, proofing, reconciliação, `Telemetry`. Acessa o banco pela abstração `IOrchestratorDb`, sem repositórios (D-010). |
| `src/Orchestrator.Infrastructure` | biblioteca | Application | Adaptadores: `DbContext` e migrações (PostgreSQL), RabbitMQ (publisher e consumidores), S3, busca de documento, envio de callbacks com conexão protegida, providers (simulado, DocuSign, Lacuna, router, decorador de métricas), identidade simulada, notificador simulado, reconciliador, observabilidade e a injeção de dependências. |
| `src/Orchestrator.Api` | web (Minimal API) | Infrastructure | Endpoints, middlewares, JWT e RBAC (`Security.cs`), rate limit, health, Swagger, migração na inicialização. |
| `src/Orchestrator.Worker` | worker (host genérico) | Infrastructure | Hospeda publisher, consumidores, reconciliador e retenção. Sem HTTP. |
| `tools/CallbackSink` | web | Application | Receptor de exemplo para callbacks (não faz parte da plataforma): `POST /hook`, `GET` e `DELETE /events`, `GET /health`. |
| `tests/Orchestrator.UnitTests` | xUnit | Domain, Application, Infrastructure | Testes unitários (EF Core em memória, servidores locais para SSRF e providers). |
| `tests/Orchestrator.IntegrationTests` | xUnit | Api, Infrastructure | Ponta a ponta com PostgreSQL e RabbitMQ reais por Testcontainers, JWT local e servidores falsos de DocuSign e Lacuna (`FakeVendors`). Inclui `LiveProviderTests`, que pulam sem credenciais. |
| `portal/` | Node 20, React 18, Vite 5 | — | SPA operacional; testes com Vitest, Testing Library e jsdom. |

Dependências: `Domain ← Application ← Infrastructure ← {Api, Worker}`; `Application ← CallbackSink`.

Pacotes principais: EF Core 8 com Npgsql e convenção de nomes `snake_case`, RabbitMQ.Client 6.8, AWSSDK.S3 3.7, OpenTelemetry 1.19 (OTLP, HttpClient e ASP.NET Core), Serilog (console e OTLP), Swashbuckle 6.6, JwtBearer 8. O ambiente `portal` usa `react-router-dom` 6.

## 3. Modelo de dados

PostgreSQL 16. Nomes em `snake_case`; enums gravados como texto; carimbos de tempo `timestamptz`. Ids são `text` gerados na aplicação, com prefixo e monotônicos (milissegundos Unix, contador e sufixo aleatório, D-021): `sig_`, `sgn_`, `op_`, `evt_`, `msg_`, `art_`, `upl_`, `cbd_`, `dlq_`, `cnf_`, `ntf_`, `cor_`, `recon_` (a sessão de proofing usa `prf_` e as validações `ivl_`).

Há apenas **duas chaves estrangeiras** reais: `signer.process_id` e `identity_validation.session_id` (ambas em cascata). A chave de `operation` para `signature_process` foi removida porque operações também pertencem a sessões de proofing (D-044). As demais referências a `process_id` são lógicas.

### Tabelas

| Tabela | Conteúdo | Pontos notáveis |
|---|---|---|
| `signature_process` | Processo: `external_id`, `business_status`, `operational_status`, `signature_type`, `request_json`, `callback_json`, `identity_validations_json`, `correlation_id`, `version`, `document_file_name`, `client_id`, `last_reconciled_at`, datas | `version` é o token de concorrência otimista, incrementado a cada alteração. Índices em `external_id` e `created_at` (não únicos). |
| `signer` | Nome, CPF, posição, `signed`, `signed_at`, `email`, `phone`, `signature_type`, `sign_order`, `confirmation_channels` (lista separada por vírgula), `released_at` | FK para o processo. |
| `signer_confirmation` | Por signatário e canal: `status`, `code_hash` (HMAC do código), `expires_at`, `failed_attempts`, `send_count`, `last_sent_at`, `confirmed_at`, `version` | Único `(signer_id, channel)`. O código em claro nunca é gravado. |
| `operation` | `process_id` (processo **ou** sessão), `type`, `status`, `attempt`, `max_attempts`, `next_retry_at`, `input_json`, `output_json`, `error_json`, `error_class`, `sequence` | Único `(process_id, type, sequence)`. |
| `journal_event` | `seq` (identidade global), `process_id`, `type`, `occurred_at`, `actor_type`, `actor_id`, `metadata_json`, `correlation_id`, `causation_id` | **Append-only por trigger** (ver abaixo). |
| `outbox_event` | `aggregate_id`, `type`, `payload_json`, `queue`, `available_at`, `published_at`, `attempts` | Índice parcial das não publicadas por `available_at`. |
| `inbox_message` | `(consumer, message_id)`, `received_at` | Chave composta. |
| `idempotency_record` | `key` (derivada), `request_hash`, `process_id`, `created_at` | Retenção de 24 h aplicada ao reutilizar a chave. |
| `provider_process` | `process_id`, `provider_code`, `provider_process_id`, `external_reference`, `normalized_status`, `metadata_json` | Único em `external_reference`; `provider_code` fixa o provider do processo. |
| `artifact` | `process_id`, `type`, `content_type`, `sha256`, `size`, `storage_key`, `file_name`, `metadata_json` | Único `(process_id, type)`. |
| `dead_letter_entry` | `process_id`, `operation_id`, `operation_type`, `domain`, `queue`, `error_class`, `reason`, `attempts`, `resolved_at`, `resolved_by` | Índices por `(domain, resolved_at)` e `operation_id`. |
| `callback_registration` | `callback_id` (chave), `url`, `secret`, `active`, `description`, `client_id` | `secret` em texto (exibido só na criação). |
| `callback_delivery` | `process_id`, `operation_id`, `event_id`, `event_type`, `process_status`, `destination`, `status`, `attempts`, `last_status_code`, `last_error`, datas | Únicos em `operation_id` e `event_id`. |
| `proofing_session` | `external_id`, `status`, `result`, `subject_json`, `client_id`, `evidence_deleted_at`, datas | Índices por `created_at` e `(status, completed_at)`. |
| `identity_validation` | `capability`, `required`, `status`, `score`, `details_json`, `provider_code`, `operation_id` | Único `(session_id, capability)`; FK para a sessão. |
| `reconciliation_record` | `process_id`, `trigger`, `internal_status`, `provider_status`, `outcome`, `resulting_status`, `details_json` | Só correções e chamadas manuais (D-051). |
| `document_upload` | `client_id`, `file_name`, `content_type`, `size`, `sha256`, `storage_key` (`uploads/<id>`) | Validade lógica de 24 h; sem limpeza. |
| `notification_sink` | Destino e código em claro | **Só desenvolvimento** (notificador simulado); lido pelo endpoint de desenvolvimento com `Confirmation:ExposeSink`. |

**JSONB** é usado em `request_json`, `callback_json`, `identity_validations_json`, `input_json`, `output_json`, `error_json`, `metadata_json` (journal, artefato e provider), `payload_json`, `subject_json`, `details_json`. Não há índices GIN.

**Concorrência.** Token otimista `version` em `signature_process` e `signer_confirmation` (sem trava de linha); o motor repete até 5 vezes em conflito, a reconciliação até 12 com espera aleatória de 5 a 40 ms (D-054). Exceção: as validações de identidade usam `SELECT ... FOR UPDATE` na linha da sessão dentro de transação explícita (D-043).

**Journal append-only.** A migração `JournalAppendOnly` cria a função `journal_event_immutable()` e dois triggers: `BEFORE UPDATE OR DELETE` por linha e `BEFORE TRUNCATE` por comando, ambos lançando exceção. O trigger é SQL cru e não aparece no modelo do EF. Não há papel de banco separado: o dono do banco pode removê-lo.

### Migrações

| # | Migração | O que faz |
|---|---|---|
| 1 | `InitialCreate` | Idempotência, inbox, journal, outbox, `provider_process`, `signature_process`, `operation`, `signer`. |
| 2 | `JournalAppendOnly` | Triggers que tornam o journal imutável. |
| 3 | `AddArtifacts` | Tabela `artifact`. |
| 4 | `AddResilience` | `operation.error_class` e `dead_letter_entry`. |
| 5 | `AddCallbacks` | `callback_registration` e `callback_delivery`. |
| 6 | `AddIdentityProofing` | Remove a FK de `operation`; cria `proofing_session` e `identity_validation`. |
| 7 | `AddReconciliation` | `last_reconciled_at` e `reconciliation_record`. |
| 8 | `AddPortalSupport` | `document_file_name`, preenchido a partir de `request_json`. |
| 9 | `AddClientSegregation` | `signature_process.client_id`. |
| 10 | `AddClientSegregationProofingCallbacks` | `client_id` em sessões e callbacks cadastrados. |
| 11 | `AddSignerConfirmation` | Colunas novas em `signer`; `signer_confirmation`, `document_upload`, `notification_sink`. |

A **API aplica as migrações** na inicialização (até 30 tentativas a cada 2 s, desligável por `Database:MigrateOnStartup`). O **Worker não migra**: cada consumidor espera o esquema ficar sem migração pendente. Há uma fábrica de tempo de projeto para `dotnet ef`.

## 4. Filas, DLQs e mensagens

RabbitMQ 3.13. A topologia é declarada de forma idempotente pelo publisher e por cada consumidor.

- Duas exchanges **direct** e duráveis: `orchestrator.commands` e `orchestrator.dlx`.
- Para cada domínio: uma **fila de comando** durável com o nome do domínio (ligada a `orchestrator.commands`, chave igual ao nome) e uma **DLQ** `<domínio>-dlq` (ligada a `orchestrator.dlx`). A fila de comando aponta sua dead-letter exchange para `orchestrator.dlx` com a chave `<domínio>-dlq`.
- Sem TTL, sem limite de tamanho, sem *quorum queues* e sem *delayed exchange*: **retry e polling usam o atraso da própria linha de outbox** (`available_at`), não filas de retry.

| Domínio (fila) | DLQ | Operações |
|---|---|---|
| `signature-provider` | `signature-provider-dlq` | `PROVIDER_CREATE_PROCESS`, `PROVIDER_SEND_DOCUMENT`, `PROVIDER_STATUS_CHECK` |
| `artifact` | `artifact-dlq` | `DOCUMENT_DOWNLOAD`, `DOCUMENT_STORE`, `SIGNED_DOCUMENT_DOWNLOAD`, `SIGNED_DOCUMENT_STORE` |
| `identity-proofing` | `identity-proofing-dlq` | `IDENTITY_VALIDATION` |
| `callback` | `callback-dlq` | `CALLBACK_SEND`, `OUTPUT_DELIVERY` (sem executor) |
| `notification` | `notification-dlq` | `CONFIRMATION_SEND`, `CONFIRMATION_VERIFY` (só o envio tem executor) |

**Quem alimenta a DLQ.** (1) A aplicação, ao esgotar as tentativas: grava `dead_letter_entry` e enfileira no outbox uma mensagem `OPERATION_DEAD_LETTERED` para `<domínio>-dlq`. (2) O broker, quando uma mensagem malformada (JSON inválido, sem `operationId` ou sem `message-id`) é rejeitada sem reenfileirar. **Nenhum processo consome as DLQs**; a fonte operacional é a tabela `dead_letter_entry`, consultada por `GET /v1/dead-letters` e resolvida por reprocessamento ou reconciliação.

**Consumidores.** Um serviço de segundo plano por fila, cada um com conexão e canal próprios, recuperação automática (rede reaberta a cada 2 s), `prefetch` de 10 e confirmação manual. Por padrão o Worker consome as cinco filas (`RabbitMq:Queues` permite escolher). *Nota:* a decisão D-026 dizia que o Worker consumia só `signature-provider` e `artifact`; o código atual consome as cinco.

| Resultado da execução | Ação no broker |
|---|---|
| Qualquer resultado do motor (executada, duplicada, ignorada, falha, retry agendado, DLQ) | `ack` |
| Exceção inesperada, inclusive falha de infraestrutura (`DbException`, `DbUpdateException`) | Espera 1 s e `nack` com reenfileiramento; não consome tentativa nem grava inbox (D-028, D-031) |
| Mensagem malformada | `nack` sem reenfileirar, vai à DLQ do domínio |

**Formato.** Corpo JSON persistente, **só referências**:

```json
{ "processId": "sig_...", "operationId": "op_...", "correlationId": "cor_...", "causationId": "evt_...", "traceparent": "00-..." }
```

(`processId` pode ser o id de uma sessão de proofing.) A mensagem de DLQ leva `processId`, `operationId`, `deadLetterId`, `domain`, `errorClass` e os ids de correlação. Propriedades AMQP: `message-id` (o id da linha de outbox, chave de deduplicação do inbox), `type` (`OPERATION_REQUESTED` ou `OPERATION_DEAD_LETTERED`), `correlation-id`, modo persistente, e os cabeçalhos `traceparent` e `causationId`.

## 5. Outbox, inbox e execução de uma operação

### Outbox

- **Escrita**: junto da mudança de estado, na mesma unidade de trabalho. O `available_at` é o instante atual, ou futuro para retry e polling.
- **Publicação** (`OutboxPublisher`, no Worker): em laço, abre uma transação e seleciona até 50 linhas não publicadas com `available_at` vencido, ordenadas, com `FOR UPDATE SKIP LOCKED` (seguro com várias instâncias). Para cada linha publica na exchange da fila e **espera a confirmação do broker (até 10 s)**; marca `published_at` e incrementa `attempts`; ao fim, `SaveChanges` e `Commit`. Sem linhas, espera 300 ms.
- **Falha**: qualquer exceção reabre a conexão após 2 s. Como a transação só confirma no fim, uma falha no meio do lote faz reversão e as mensagens já publicadas serão publicadas de novo (*at-least-once*); o inbox as deduplica.
- Linhas publicadas **não são apagadas** (não há limpeza de outbox).

### Inbox

Chave `(consumer, message_id)`. O consumidor é sempre `provider-worker`, inclusive para as demais filas, e o `message_id` é o `message-id` AMQP (o id da linha de outbox). Reprocessos e reagendamentos criam **nova** linha de outbox, com novo id, e por isso não colidem. Violação de unicidade na corrida resolve como duplicata. Também sem limpeza.

### Execução de uma operação (D-011)

Uma execução é **uma transação** (`WorkflowEngine`):

1. O consumidor valida a mensagem, abre o span `operation.receive` e chama o motor, que abre `operation.execute`.
2. Até 5 tentativas em conflito de concorrência (token `version`); violação de unicidade no inbox resolve como duplicata.
3. Se já existe inbox para a mensagem, responde duplicada (`ack`).
4. Adiciona o inbox ao contexto, carrega operação e processo. Operação inexistente ou fora de `NOT_STARTED`/`RETRY_PENDING` salva só o inbox e é ignorada. Processo terminal cancela a operação (exceto `CALLBACK_SEND`).
5. Incrementa a tentativa e, para operações principais, põe o processo em `PROCESSING`. O estado `PROCESSING` da operação não é persistido.
6. Executa o handler. Efeitos externos (gravar objeto, chamar provider, enviar callback ou notificação) acontecem **dentro da execução, fora da transação do banco**; o handler altera entidades, grava eventos, cria a próxima operação e a mensagem de outbox (com `available_at`) e, quando cabe, a entrega de callback e sua operação.
7. **Sucesso**: operação `COMPLETED`, processo de volta a `READY`, e **um único `SaveChanges`** grava tudo, inclusive o inbox. Depois, `ack`.
8. **Falha classificada**: descarta as mudanças parciais, regrava o inbox e classifica o erro. `PERMANENT`: operação `FAILED` e processo `MANUAL_ACTION`. `TRANSIENT` ou `UNKNOWN` com tentativas restantes (`UNKNOWN` limitado a 3): operação `RETRY_PENDING`, `next_retry_at` e nova mensagem de outbox com `available_at` no horário do retry. Tentativas esgotadas: `DLQ`, `dead_letter_entry` e mensagem para `<domínio>-dlq`. Tudo no mesmo `SaveChanges`. Depois, `ack`.
9. **Falha de infraestrutura**: propaga; nada é gravado; `nack` com reenfileiramento.

Garantia: o efeito no banco e o inbox são atômicos; os efeitos externos são *at-least-once*, e por isso os adapters são idempotentes pela referência externa e o armazenamento é idempotente por processo e tipo. As operações de proofing seguem o mesmo caminho, com transação explícita e trava da linha da sessão.

### Criação de processo

A API grava, em **um** `SaveChanges`: processo, signatários, confirmações planejadas, a operação `DOCUMENT_DOWNLOAD`, o registro de idempotência, o evento `PROCESS_CREATED` e a mensagem de outbox. A corrida de duas requisições com a mesma chave resolve pela unicidade do registro de idempotência: a perdedora recebe o replay.

## 6. Workers

`AddOrchestratorCore` roda em API e Worker; os serviços de segundo plano de cada host:

| Serviço | Processo | Função |
|---|---|---|
| `OutboxPublisher` | Worker | Publica a outbox no RabbitMQ (300 ms, lote de 50). |
| `OperationConsumer` ×5 | Worker | Um por fila; executa o `WorkflowEngine`. Espera o esquema do banco antes de iniciar. |
| `ReconciliationWorker` | Worker | Se `Reconciliation:Enabled`, a cada `IntervalSeconds` reconcilia até `BatchSize` processos candidatos (em `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED`, com registro no provider e sem conferência há mais de `StaleAfterSeconds`), cada um em escopo próprio. |
| `EvidenceRetentionService` | Worker | A cada `Identity:RetentionSweepMinutes` apaga evidências (objeto e registro) de sessões `COMPLETED` com mais de `Identity:EvidenceRetentionDays` dias, até 100 sessões por varredura. |
| `DeadLetterGaugeService` | API e Worker | A cada 10 s conta as dead letters pendentes e atualiza o medidor `orchestrator.deadletter.size`. Roda nos dois processos. |

Não há serviço de limpeza de outbox, inbox, journal, uploads ou idempotência, nem de expiração de processos. Os **webhooks de provider** reconciliam de forma síncrona dentro da requisição da API, não por fila. Pipeline HTTP da API, em ordem: correlação, tratamento de erros (`problem+json`), autenticação e controle de acesso (se `Auth:Enabled`), rate limit, ator de auditoria, Swagger, `/health` e endpoints.

## 7. Fluxo de um processo de assinatura

```text
API: POST /v1/signature-processes
  └─ processo CREATED + operação DOCUMENT_DOWNLOAD + outbox            (1 transação)

artifact           DOCUMENT_DOWNLOAD   busca URL ou upload, guarda ORIGINAL_DOCUMENT   CREATED → DOCUMENT_RECEIVED
artifact           DOCUMENT_STORE      confere o original                               DOCUMENT_RECEIVED → VALIDATING
signature-provider PROVIDER_CREATE_PROCESS   cria no provider (idempotente)            VALIDATING → READY_FOR_SIGNATURE
signature-provider PROVIDER_SEND_DOCUMENT    envia (ou adia até as confirmações)       READY_FOR_SIGNATURE → SIGNATURE_IN_PROGRESS
signature-provider PROVIDER_STATUS_CHECK     consulta e se reagenda a cada 2 s         → PARTIALLY_SIGNED → SIGNED
artifact           SIGNED_DOCUMENT_DOWNLOAD  baixa assinado e evidência                SIGNED → FINALIZING
artifact           SIGNED_DOCUMENT_STORE     confere e conclui                         FINALIZING → COMPLETED

callback           CALLBACK_SEND       emitido a cada mudança notificável, em paralelo ao fluxo
notification       CONFIRMATION_SEND   emitido quando é a vez do signatário, em paralelo ao fluxo
```

Em paralelo, o `ReconciliationWorker` e os webhooks podem avançar o processo por correção (seção 18 do PRD).

## 8. Armazenamento de objetos

- API S3 por `AWSSDK.S3`, com endereçamento por caminho (`ForcePathStyle`) e região de autenticação `us-east-1`. O código só usa a API S3, então o backend é trocável (MinIO no compose, D-020).
- Bucket único `signature-artifacts`, criado pelo serviço `minio-init` do compose e, se faltar, criado pela aplicação uma vez por processo.
- Operações: gravar, ler, apagar e consultar existência. O conteúdo é carregado inteiro em memória, limitado por `Artifacts:MaxDocumentBytes` (50 MiB).
- Chaves: `{processId}/input/original`, `{processId}/output/signed`, `{processId}/evidence/evidence.json`, `{sessionId}/identity/document-front`, `.../document-back`, `.../selfie`, `uploads/{uploadId}`. Nunca expostas pela API.
- O hash SHA-256 é calculado na gravação e guardado em `artifact.sha256`.
- Falhas de leitura e gravação são transitórias. Não há política de ciclo de vida do bucket nem criptografia configurada pelo código; só evidências de proofing são removidas.

## 9. Configuração por variáveis de ambiente

Em .NET, `__` separa níveis (`Retry__Default__MaxAttempts`). Precedência: `appsettings.json`, `appsettings.{Ambiente}.json`, variáveis de ambiente. API e Worker leem um arquivo `.env` (do diretório atual ou de um acima) e definem as variáveis **só se ainda não existirem**; o `.env` está fora do git e do Docker (`.env.example` documenta as variáveis dos providers). A = lida pela API, W = pelo Worker; sem marca, ambos.

### Banco, broker e storage

| Chave | Padrão | Finalidade |
|---|---|---|
| `ConnectionStrings__Postgres` | `localhost:5433` (appsettings) | Conexão do PostgreSQL. O compose usa o host `postgres`. |
| `Database__MigrateOnStartup` (A) | `true` | Aplica migrações na inicialização. |
| `RabbitMq__Host`, `Port`, `User`, `Password`, `VirtualHost` (W) | `localhost`, 5672 | Conexão do broker. |
| `RabbitMq__Prefetch` (W) | 10 | Mensagens não confirmadas por consumidor. |
| `RabbitMq__OutboxBatchSize`, `OutboxPollMs` (W) | 50, 300 | Lote e intervalo do publisher. |
| `RabbitMq__Queues__N` (W) | as 5 filas | Filas consumidas por esta instância. |
| `S3__Endpoint`, `AccessKey`, `SecretKey`, `Bucket`, `Region` | `localhost:9000`, `signature-artifacts`, `us-east-1` | Object storage. Credenciais de desenvolvimento no compose. |

### Autenticação e limites (API)

| Chave | Padrão | Finalidade |
|---|---|---|
| `Auth__Enabled` | `false` (compose: `true`) | Liga JWT, RBAC e segregação. Desligado, a API é aberta e o ator vem de `X-Operator-Id`. |
| `Auth__Authority` | — | Descoberta OIDC das chaves de assinatura. |
| `Auth__ValidIssuer` | — | `iss` esperado (difere do *authority* por causa do nome do host no compose). |
| `Auth__Audience` | — | `aud` esperado (`orchestrator-api` no compose). |
| `Auth__RequireHttpsMetadata` | `false` | Exige HTTPS ao buscar metadados. |
| `Auth__SigningKey` | — | Chave simétrica só para testes e uso offline; substitui a validação por *authority*. |
| `RateLimit__CreatePerMinute` | 600 | Limite de criação por cliente (janela fixa, por instância). |

### Fluxo, retry, idempotência e SLA

| Chave | Padrão | Finalidade |
|---|---|---|
| `Workflow__PollIntervalSeconds` | 2 | Intervalo do `PROVIDER_STATUS_CHECK`. |
| `Idempotency__RetentionHours` | 24 | Validade da chave de idempotência. |
| `Retry__Jitter` | 0,2 | Jitter relativo (±20%). |
| `Retry__UnknownMaxAttempts` | 3 | Limite para erros desconhecidos. |
| `Retry__Default__MaxAttempts`, `DelaysSeconds__N`, `BaseDelaySeconds`, `Multiplier`, `MaxDelaySeconds` | 8 tentativas; `0, 5, 30, 120, 600, 1800, 7200, 21600` s | Política padrão (no código, não no `appsettings`, por causa do comportamento do binder com listas, D-032). O compose usa 4 tentativas e 1 s só para demonstração. |
| `Retry__Operations__<TIPO>__*` | — | Sobrescreve por tipo de operação. |
| `Sla__SigningMinutes` | 60 | Limite do indicador `ALERT` do SLA. |

### Callbacks, artefatos e documentos

| Chave | Padrão | Finalidade |
|---|---|---|
| `Callbacks__DefaultSecret` | segredo de desenvolvimento | Chave HMAC de callbacks por URL dinâmica. |
| `Callbacks__AllowHttp`, `AllowPrivateNetworks` | `false` (compose: `true`) | Relaxam a política de SSRF (só desenvolvimento). |
| `Callbacks__AllowedHosts__N` | vazio | Allowlist de hosts (aceita `*.dominio`). |
| `Callbacks__TimeoutSeconds`, `RateLimitPerSecond`, `SignatureToleranceSeconds` | 10, 10, 300 | Timeout, limite por host e tolerância de relógio. |
| `Artifacts__PublicBaseUrl` | `http://localhost:8080` | Base das URLs de download. |
| `Artifacts__SigningKey` | segredo de desenvolvimento | Chave HMAC dos links. |
| `Artifacts__LinkTtlSeconds` | 300 | Validade do link. |
| `Artifacts__MaxDocumentBytes` | 52428800 | Limite de documento, artefato e upload. |
| `Artifacts__FetchTimeoutSeconds` | 30 | Timeout da busca do documento por URL. |
| `Artifacts__BlockPrivateNetworks` | `false` | Aplica a guarda de SSRF à busca do documento (até 3 redirecionamentos, HTTP permitido). |

### Confirmação, identidade, reconciliação e providers

| Chave | Padrão | Finalidade |
|---|---|---|
| `Confirmation__CodeLength`, `CodeTtlSeconds`, `MaxAttempts`, `ResendMinIntervalSeconds`, `MaxSends` | 6, 600, 5, 30, 5 | Parâmetros do código. |
| `Confirmation__HashKey` | segredo de desenvolvimento | Chave do HMAC do código; **o compose não a sobrescreve**. |
| `Confirmation__ExposeSink` | `false` (compose: `true` na API) | Habilita o endpoint de desenvolvimento dos códigos. |
| `Identity__MaxEvidenceBytes`, `EvidenceRetentionDays`, `RetentionSweepMinutes` | 5242880, 30, 60 | Evidências de proofing. |
| `Reconciliation__Enabled`, `IntervalSeconds`, `StaleAfterSeconds`, `BatchSize` | `true`, 60, 120, 50 | Reconciliador (compose: 10 s e 20 s). |
| `Provider__Default` | `Simulated` | Provider dos **novos** processos: `Simulated`, `DocuSign` ou `Lacuna`; valor desconhecido falha na inicialização. |
| `FakeProvider__Mode`, `SignDelaySeconds` | `Auto`, 2 | Comportamento do provider simulado. |
| `DOCUSIGN_INTEGRATION_KEY`, `DOCUSIGN_USER_ID`, `DOCUSIGN_ACCOUNT_ID` | vazio | Identificadores DocuSign. |
| `DOCUSIGN_PRIVATE_KEY`, `DOCUSIGN_PRIVATE_KEY_FILE` | vazio | Chave RSA do JWT Grant (PEM ou caminho; o arquivo vence). **Segredo.** |
| `DOCUSIGN_BASE_URI`, `DOCUSIGN_AUTH_SERVER` | ambiente de demonstração | Endereços REST e OAuth. |
| `DOCUSIGN_CONNECT_HMAC_KEY` | vazio | Chave do webhook; sem ela, todo webhook DocuSign é rejeitado. |
| `LACUNA_API_KEY`, `LACUNA_BASE_URI`, `LACUNA_WEBHOOK_SECRET` | vazio, ambiente de demonstração | Credencial, endereço e segredo do webhook da Lacuna. |

Credenciais ausentes só falham quando o provider correspondente é selecionado (resolução sob demanda).

### Telemetria e logs

| Chave | Padrão | Finalidade |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` (ou `Otel__Endpoint`) | vazio (não exporta) | Base OTLP por HTTP; o código acrescenta `/v1/traces`, `/v1/metrics` e `/v1/logs`. |
| `OTEL_METRIC_EXPORT_INTERVAL` | 60 s (SDK); compose: 5000 ms | Intervalo de exportação de métricas. |
| `Serilog__*`, `Logging__LogLevel__*` | appsettings | Níveis de log. |
| `ASPNETCORE_URLS` | `http://+:8080` nas imagens | Porta de escuta. |

## 10. Serviços do docker-compose e portas

Projeto `digital-signature`. Uma âncora de ambiente (`x-app-env`) é compartilhada por `api` e `worker`. As credenciais dos serviços (PostgreSQL, MinIO, administrador do Keycloak) são de desenvolvimento e vivem no próprio compose.

| Serviço | Imagem | Portas (host:contêiner) | Depende de | Observações |
|---|---|---|---|---|
| `postgres` | `postgres:16-alpine` | **5433**:5432 | — | Volume `pgdata`; healthcheck `pg_isready`. |
| `rabbitmq` | `rabbitmq:3.13-management-alpine` | 5672:5672, **15672**:15672 (interface) | — | Sem volume; healthcheck de ping. |
| `minio` | `bitnamilegacy/minio:2025.5.24` | **9000**:9000 (S3), **9001**:9001 (console) | — | Volume `miniodata`; imagem legada porque as oficiais não são mais baixáveis (D-020). |
| `minio-init` | `bitnamilegacy/minio-client` | — | minio (saudável) | Cria o bucket e termina. |
| `sample-docs` | `nginx:alpine` | (interno) | — | Serve `contrato.pdf` em `http://sample-docs/`. |
| `callback-sink` | build `deploy/Sink.Dockerfile` | **8081**:8080 | — | Receptor de callbacks de exemplo (D-038). |
| `keycloak` | `quay.io/keycloak/keycloak:25.0` | **8180**:8080 | — | `start-dev` com importação do realm; sem volume (dados se perdem ao recriar). |
| `otel-collector` | `otel/opentelemetry-collector-contrib:0.104.0` | **8889**:8889 (métricas Prometheus) | — | Receptores OTLP 4317 e 4318 só na rede interna. |
| `api` | build `deploy/Api.Dockerfile` | **8080**:8080 | postgres e minio (saudáveis) | Sem healthcheck; não usa o RabbitMQ. |
| `worker` | build `deploy/Worker.Dockerfile` | — | postgres, rabbitmq, minio (saudáveis); sample-docs, callback-sink, api | Sem healthcheck; `restart: on-failure`. |
| `portal` | build `./portal` | **3000**:80 | api, keycloak | nginx servindo a SPA. |

Em desenvolvimento do portal: Vite na porta 5173, com proxy de `/v1` para a API e de `/realms` para o Keycloak. Outras portas citadas no ambiente local: Swagger e `/health` na API (8080).

Ajustes de demonstração no compose: tentativas e atrasos de retry curtos, reconciliação a cada 10 s com obsolescência de 20 s, `Callbacks__AllowHttp` e `AllowPrivateNetworks` ligados, `Confirmation__ExposeSink` ligado na API, `Auth__Enabled` ligado, `Provider__Default` vindo do `.env` (padrão `Simulated`). Não há healthchecks para `api`, `worker`, `portal`, `keycloak`, `otel-collector` e `callback-sink`.

**Dockerfiles** (`deploy/`): API e Worker constroem com `mcr.microsoft.com/dotnet/sdk:8.0` e executam em `aspnet:8.0` e `runtime:8.0`; o portal constrói com `node:20-alpine` e serve com `nginx:1.27-alpine`. Nenhum define usuário não-root, `HEALTHCHECK` ou fixação por digest.

## 11. Identidade, coletor OpenTelemetry e proxy do portal

**Realm do Keycloak** (`deploy/keycloak/realm-orchestrator.json`): exige TLS desativado (`sslRequired: none`), token de acesso de 900 s, papéis `viewer`, `operator`, `client` e `admin`. Clientes: `portal` (público, authorization code com PKCE S256, sem password grant, redirecionamentos para as portas 3000 e 5173), `orchestrator-cli` (público, password grant, **só desenvolvimento**, para scripts e smoke), `orchestrator-client-a` e `orchestrator-client-b` (confidenciais, client credentials). Todos adicionam `aud=orchestrator-api`. Usuários de demonstração: `viewer`, `operator` e `admin` (este com os papéis `admin` e `operator`). A API lê os papéis de `realm_access.roles` e segrega por `azp`.

**Coletor OTel** (`deploy/otel/collector.yaml`): receptor OTLP (HTTP 4318 e gRPC 4317), processador `batch`, exportadores `prometheus` (porta 8889, expiração de métricas de 10 min) e `debug`. Pipelines: **métricas** para o Prometheus; **traces e logs** só para o `debug` (saída padrão do coletor). Não há Prometheus, Grafana, Jaeger, Tempo ou Loki no compose.

**nginx do portal** (`portal/nginx.conf`): `/v1/` com proxy para `http://api:8080` (`client_max_body_size 20m`); `/realms/` com proxy para o Keycloak (só o endpoint de token, mesma origem, sem CORS); `/` serve a SPA com *fallback* para `index.html`. O endpoint de autorização do Keycloak é acessado direto pelo navegador (`VITE_AUTH_URL` ou `<host>:8180`).

## 12. Observabilidade

OpenTelemetry com fonte e medidor `Orchestrator`; serviços `orchestrator-api` e `orchestrator-worker`; exportação OTLP por HTTP apenas se houver endpoint. Instrumentação automática: HTTP de entrada (API, sem `/health`) e HttpClient. Não há instrumentação de EF Core, Npgsql nem do cliente AMQP.

**Spans**

| Span | Origem | Tags |
|---|---|---|
| `operation.receive` | Consumidor (pai: `traceparent` da mensagem) | `correlation.id`, `process.id`, `operation.id`, `messaging.queue` |
| `operation.execute` | Motor de workflow | `correlation.id`, `operation.id`, `provider.id`, `operation.type`, `process.id` |
| `provider.<chamada>` | Decorador do provider (`create_process`, `send_document`, `add_signer`, `release_signer`, `get_status`, `cancel`, `download_signed`, `download_evidence`) | `provider.id`; erro registrado |

**Métricas** (medidor `Orchestrator`)

| Instrumento | Tipo | Marcadores |
|---|---|---|
| `orchestrator.process.created`, `.completed` | contador | — |
| `orchestrator.process.failed` | contador | `status` |
| `orchestrator.process.completion_time` | histograma (s) | — |
| `orchestrator.provider.latency` | histograma (ms) | `provider`, `call` |
| `orchestrator.provider.errors` | contador | `provider`, `call`, `transient` |
| `orchestrator.operation.retries` | contador | `operation.type` |
| `orchestrator.callback.deliveries` | contador | `result` |
| `orchestrator.reconciliation.runs` | contador | `outcome`, `trigger` |
| `orchestrator.proofing.failures` | contador | `capability` |
| `orchestrator.security.denied` | contador | `reason` |
| `orchestrator.deadletter.size` | medidor | — |

Contadores de processo são emitidos depois do `SaveChanges`, na API (criação) e no Worker (conclusão): cada série vem de um `service.name` distinto. O exportador Prometheus normaliza os nomes (pontos para sublinhado); o nome final exato não foi verificado.

**Logs e correlação.** Serilog para console e OTLP, com `TraceId`, `SpanId`, `ProcessId`, `OperationId` e `CorrelationId`. O `X-Correlation-Id` é aceito ou gerado, devolvido, gravado no journal e nas mensagens, e o `causationId` encadeia os eventos. O `traceparent` é gravado no payload do outbox e retomado no consumidor. CPF, telefone e e-mail saem mascarados; códigos de confirmação e conteúdo de evidências nunca são registrados.

**Não existe**: alertas, dashboards, métricas de fila e de atraso do outbox, métricas de runtime, saúde do Worker, saúde de RabbitMQ e S3 na API (o `/health` só testa o PostgreSQL).

## 13. Segurança de infraestrutura

- **SSRF de callbacks**: validação estática (URL absoluta, HTTPS, sem credenciais, allowlist, bloqueio de `localhost` e IP literal privado) no cadastro, na criação do processo e no envio; e **validação na conexão**: o DNS é resolvido uma vez, a conexão é feita direto a um IP validado e qualquer IP resolvido bloqueado derruba a tentativa (mitiga DNS rebinding). Faixas bloqueadas: loopback, `0.0.0.0`, `10/8`, `100.64/10` (CGNAT), `169.254/16` (metadados de nuvem), `172.16/12`, `192.168/16`, `192.0.0/24`, `198.18/15`, multicast e reservados, e os equivalentes IPv6. Sem redirecionamentos; timeout de conexão de 10 s; reconexão a cada minuto para reavaliar o DNS; corpo da resposta nunca lido.
- **Assinaturas**: callbacks com HMAC-SHA256 de `"{timestamp}.{corpo}"`; links de download com HMAC-SHA256 de `"{artifactId}.{expires}"`; webhooks do DocuSign com HMAC do corpo e da Lacuna com segredo compartilhado, ambos em tempo constante; códigos de confirmação guardados como HMAC. Webhooks não têm proteção contra replay.
- **DocuSign**: OAuth JWT Grant com asserção RS256 e token em cache até cerca de 2 minutos antes de expirar; chave e token nunca são registrados.
- **Autorização**: JWT Bearer (emissor, audiência, validade, tolerância de 30 s) e RBAC por rota; segregação por `azp`. Rotas públicas: `/health`, `/swagger`, `/v1/downloads`, `/v1/webhooks`.
- **Segredos**: variáveis de ambiente e `.env` fora do git. Não há secrets manager, criptografia em repouso, terminação TLS nem CORS no código.
- **Delegado à infraestrutura externa**: TLS e WAF, criptografia em repouso e KMS, rotação de segredos, rate limit amplo, backup e alta disponibilidade de PostgreSQL, RabbitMQ e storage, ciclo de vida do bucket, imagens sem root, e papel de banco separado para reforçar o append-only.

## 14. Pontos de atenção

1. D-026 está desatualizada: o Worker consome as cinco filas por padrão.
2. As DLQs são alimentadas, mas ninguém as consome; a tabela `dead_letter_entry` é a fonte operacional.
3. Retry e polling usam `available_at` no outbox; a precisão do atraso depende do intervalo do publisher (300 ms) e do tamanho do lote.
4. Outbox, inbox, journal, registros de idempotência e uploads não têm limpeza e crescem sem limite; a expiração da idempotência é aplicada só ao reutilizar a chave.
5. `Artifacts__BlockPrivateNetworks` é falso por padrão: a busca do documento por URL fica sem proteção contra SSRF até ser ligado.
6. `Confirmation__HashKey` não é sobrescrita no compose (usa o padrão de desenvolvimento).
7. Sem healthchecks para `api`, `worker`, `portal`, `keycloak`, `otel-collector` e `callback-sink`; a `api` não aguarda Keycloak nem RabbitMQ (não usa o RabbitMQ).
8. O medidor `deadletter.size` é emitido pela API e pelo Worker sob `service.name` distintos.
9. Webhooks de provider reconciliam de forma síncrona na requisição e sem proteção contra replay.
10. O nginx do portal limita o corpo a 20 MB, abaixo dos 50 MiB de `MaxDocumentBytes`; a API não define limites explícitos de corpo (valem os padrões do framework; efeito exato não verificado).
11. D-002 cita WireMock; não há WireMock no compose. Os servidores falsos dos providers rodam em processo, nos testes de integração.
12. Comentários e testes antigos citam "nove abas" no portal; o detalhe tem dez.

## 15. Como executar e testar

```bash
docker compose up -d --build        # sobe toda a plataforma
bash scripts/smoke-test.sh           # roteiro ponta a ponta contra o compose
docker compose down -v               # derruba e apaga os volumes

dotnet test tests/Orchestrator.UnitTests
dotnet test tests/Orchestrator.IntegrationTests   # exige Docker (Testcontainers)
cd portal && npm install && npm test
```

URLs locais, usuários de demonstração e exemplos de uso estão no [README](../README.md). Para usar DocuSign ou Lacuna em sandbox, copie `.env.example` para `.env`, preencha as variáveis do provider e defina `Provider__Default`.
