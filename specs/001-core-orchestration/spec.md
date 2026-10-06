# Feature Specification: Núcleo de Orquestração de Assinaturas

**Feature Branch**: `001-core-orchestration`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Núcleo da plataforma de orquestração de assinaturas (PRD seções 6, 11 a 13, 21 a 24, 43): criação de processo com Idempotency-Key, máquinas de estado de negócio e operacional independentes, operações, event journal append-only, outbox e inbox, APIs de consulta, cancel e provider de assinatura simulado."

## Clarifications

### Session 2026-10-06

- Q: Qual resposta HTTP a criação de processo retorna? → A: `202 Accepted` na primeira criação e `200 OK` em replay idempotente, ambos com o mesmo corpo e `Location` do processo.
- Q: Qual o contrato de erro da API? → A: `application/problem+json` (RFC 7807) com `errors` por campo e `correlationId`; conflito de idempotência `409`, transição inválida `409`, não encontrado `404`, validação `400`.
- Q: Quanto tempo uma chave de idempotência é retida? → A: 24 horas (configurável); depois disso a chave pode ser reutilizada.
- Q: Como o provider simulado decide o resultado da assinatura? → A: Por padrão assina automaticamente após atraso configurável (padrão 2 s); modos de falha/rejeição configuráveis para testes; sem chamadas externas.
- Q: Como as filas são organizadas e qual a ordenação garantida? → A: Uma fila por domínio de integração (nesta spec, `signature-provider`); sem garantia de ordem; consumidores validam o estado atual antes de agir.
- Q: Como tratar `VALIDATING`, `PARTIALLY_SIGNED`, `EXPIRED` e `REJECTED`? → A: `VALIDATING` é percorrido sem validação real; `PARTIALLY_SIGNED` ocorre com mais de um signatário e nem todos assinaram; `EXPIRED` e `REJECTED` existem na máquina de estados (`REJECTED` alcançável pelo provider simulado), sem expiração temporal nesta spec.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Criar processo de assinatura de forma idempotente (Priority: P1)

Um sistema consumidor cria um processo de assinatura informando documento, signatários, validações de identidade desejadas, tipo de assinatura e callback. Deve fornecer uma chave de idempotência. Repetir a mesma requisição (por timeout ou retry do cliente) nunca cria processos duplicados.

**Why this priority**: É a porta de entrada da plataforma; sem ela nada mais existe. Idempotência é inegociável pela constitution.

**Independent Test**: Enviar a mesma criação duas vezes com a mesma chave e verificar que o mesmo `processId` é retornado e só um processo existe; enviar a mesma chave com payload diferente e verificar o conflito.

**Acceptance Scenarios**:

1. **Given** nenhum processo prévio, **When** o consumidor cria um processo com chave de idempotência nova e payload válido, **Then** recebe aceitação com o `processId`, estado de negócio `CREATED` e estado operacional `READY`/`PROCESSING`.
2. **Given** um processo criado com a chave K, **When** o consumidor reenvia o mesmo payload com a chave K, **Then** recebe o mesmo processo, sem novo processo, evento ou operação.
3. **Given** um processo criado com a chave K, **When** o consumidor envia payload diferente com a chave K, **Then** recebe erro de conflito e nada é alterado.
4. **Given** uma requisição sem chave de idempotência, **When** enviada, **Then** é rejeitada com erro de validação.
5. **Given** payload inválido (sem signatários, sem documento, tipo de assinatura desconhecido), **When** enviado, **Then** recebe erro de validação descrevendo os campos inválidos e nada é persistido.
6. **Given** a requisição solicita provider específico por nome de fornecedor, **When** enviada, **Then** é rejeitada: o contrato só aceita capacidades.

---

### User Story 2 - Processo percorre o fluxo até a conclusão com provider simulado (Priority: P1)

Após a criação, a plataforma executa de forma assíncrona as operações do processo (recebimento do documento, criação do processo no provider, envio do documento, assinatura, consulta de status) usando um provider simulado atrás de uma interface comum, até o estado de negócio `COMPLETED`.

**Why this priority**: Demonstra o fluxo ponta a ponta e exercita máquinas de estado, operações, outbox e inbox.

**Independent Test**: Criar um processo e aguardar até `COMPLETED`, verificando operações concluídas, eventos no journal na ordem esperada e estado operacional final `READY`.

**Acceptance Scenarios**:

1. **Given** um processo `CREATED`, **When** o worker processa os eventos, **Then** o processo avança por `DOCUMENT_RECEIVED`, `VALIDATING`, `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS`, `SIGNED`, `FINALIZING` e `COMPLETED`, cada transição registrada no journal.
2. **Given** o mesmo evento entregue duas vezes ao worker, **When** processado, **Then** o segundo é ignorado e não há efeitos duplicados.
3. **Given** o provider simulado já tem um processo equivalente para a mesma referência externa, **When** a operação de criação é reexecutada, **Then** nenhum segundo processo é criado no provider.
4. **Given** estados de negócio e operacional independentes, **When** uma operação está pendente após `SIGNED`, **Then** os dois estados podem refletir isso separadamente.

---

### User Story 3 - Consultar processo, status, operações e eventos (Priority: P2)

O consumidor consulta o processo completo, apenas o status (negócio e operacional), a lista de operações com tentativas e a trilha de eventos.

**Why this priority**: Atende os critérios de aceite 6 e 8 do PRD e dá visibilidade a todo o resto.

**Independent Test**: Após criar e concluir um processo, chamar cada endpoint de consulta e validar conteúdo e paginação.

**Acceptance Scenarios**:

1. **Given** um processo existente, **When** consulta o processo, **Then** recebe identificadores, `externalId`, signatários, estados e datas.
2. **Given** um processo existente, **When** consulta operações, **Then** cada operação traz `operationId`, tipo, status, `attempt`, `maxAttempts` e `nextRetryAt`.
3. **Given** um processo existente, **When** consulta eventos, **Then** recebe a trilha em ordem cronológica com tipo, ator, `correlationId` e `causationId`.
4. **Given** um identificador inexistente, **When** consultado, **Then** recebe erro de não encontrado.

---

### User Story 4 - Cancelar um processo (Priority: P3)

O consumidor cancela um processo ainda não terminal; o processo vai para `CANCELLED` e operações pendentes deixam de ser executadas.

**Why this priority**: Completa o ciclo de vida básico.

**Independent Test**: Criar, cancelar antes da conclusão, verificar estado terminal e evento; cancelar um processo `COMPLETED` e verificar rejeição.

**Acceptance Scenarios**:

1. **Given** um processo não terminal, **When** cancelado, **Then** passa a `CANCELLED`, o evento é registrado e operações pendentes são interrompidas.
2. **Given** um processo em estado terminal, **When** cancelado, **Then** recebe erro de transição inválida.
3. **Given** cancelamento repetido de um processo já cancelado, **Then** a resposta é idempotente (sem novo evento).

---

### Edge Cases

- Duas requisições concorrentes com a mesma chave de idempotência: apenas um processo é criado.
- Falha do broker de mensageria no momento do commit: o evento permanece pendente na outbox e é publicado quando o broker volta; nenhuma criação confirmada é perdida.
- Publicação duplicada da outbox (publisher cai após publicar e antes de marcar): o consumidor ignora via inbox.
- Transição de estado inválida (ex.: `COMPLETED` → `SIGNED`): rejeitada e não registrada.
- Tentativa de alterar ou apagar eventos do journal: impossível.
- Cancelamento concorrente com avanço do fluxo: um único resultado consistente.
- Payload com campos sensíveis (documento do signatário): mascarados em logs.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST expor criação de processo de assinatura em `POST /v1/signature-processes`, aceitando documento (referência), signatários, validações de identidade por capacidade, tipo de assinatura, callback e destino de saída, conforme PRD seção 6.
- **FR-002**: O sistema MUST exigir `Idempotency-Key` na criação; mesma chave e mesmo payload MUST retornar o mesmo processo; mesma chave e payload diferente MUST retornar conflito.
- **FR-003**: O sistema MUST rejeitar payloads inválidos com erros de validação por campo e MUST rejeitar qualquer menção a fornecedor específico no contrato público.
- **FR-004**: O sistema MUST manter estado de negócio (`CREATED`, `DOCUMENT_RECEIVED`, `VALIDATING`, `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS`, `PARTIALLY_SIGNED`, `SIGNED`, `FINALIZING`, `COMPLETED`; terminais `FAILED`, `CANCELLED`, `EXPIRED`, `REJECTED`) com transições validadas.
- **FR-005**: O sistema MUST manter estado operacional independente (`READY`, `PROCESSING`, `RETRY_PENDING`, `SUSPENDED`, `DLQ`, `MANUAL_ACTION`) com transições validadas.
- **FR-006**: Toda atividade do orquestrador MUST ser registrada como operação independente com `operationId`, `processId`, tipo, status, `attempt`, `maxAttempts` e `nextRetryAt`. Tipos desta spec: `DOCUMENT_DOWNLOAD`, `DOCUMENT_STORE`, `PROVIDER_CREATE_PROCESS`, `PROVIDER_SEND_DOCUMENT`, `PROVIDER_STATUS_CHECK`, `SIGNED_DOCUMENT_DOWNLOAD`, `SIGNED_DOCUMENT_STORE`.
- **FR-007**: O sistema MUST gravar mudança de estado e evento de outbox na mesma transação, e um publicador MUST entregar os eventos ao barramento de mensagens com semântica at-least-once. Publicação direta pós-commit é proibida.
- **FR-008**: Consumidores MUST registrar mensagens processadas (inbox) por identificador único e ignorar duplicatas sem efeitos colaterais.
- **FR-009**: O sistema MUST manter um journal de eventos append-only (tipo, ator, timestamp, metadata, `correlationId`, `causationId`); atualização e remoção MUST ser impedidas.
- **FR-010**: O sistema MUST expor `GET /v1/signature-processes/{id}`, `/status`, `/operations` e `/events`, com paginação nas listas.
- **FR-011**: O sistema MUST expor `POST /v1/signature-processes/{id}/cancel`, idempotente para processo já cancelado e rejeitando processo em outro estado terminal.
- **FR-012**: O sistema MUST acessar provedores de assinatura apenas por uma interface comum de adapter (criar processo, enviar documento, adicionar signatário, consultar status, cancelar); nenhum tipo de fornecedor MAY vazar para o domínio. Um adapter simulado MUST permitir o fluxo completo e MUST ser idempotente por referência externa.
- **FR-013**: O sistema MUST preservar metadados nativos do provider junto de um status normalizado.
- **FR-014**: Mensagens no barramento MUST conter apenas referências (ids), nunca documentos ou dados sensíveis.
- **FR-015**: Valores sensíveis (documento do signatário) MUST ser mascarados em logs.
- **FR-016**: O sistema MUST rodar localmente com um único comando de composição de containers, incluindo API, worker, banco de dados e broker.
- **FR-017**: API e worker MUST ser stateless e escaláveis independentemente.
- **FR-019**: A criação MUST responder `202` (nova) ou `200` (replay); erros MUST seguir `application/problem+json`; chaves de idempotência são retidas por 24 horas.
- **FR-018**: Toda resposta e evento MUST carregar ou propagar um `correlationId`, aceito do cliente (`X-Correlation-Id`) ou gerado.

### Key Entities

- **SignatureProcess**: ciclo de vida de uma assinatura; possui `externalId`, estado de negócio, estado operacional, tipo de assinatura, callback, capacidades de identidade solicitadas e metadados.
- **Signer**: pessoa que assina, com `externalId`, nome e documento (mascarável).
- **Document**: referência ao documento original (nome e origem).
- **Operation**: unidade de trabalho retentável de um processo.
- **JournalEvent**: fato imutável ocorrido em um processo.
- **OutboxEvent / InboxMessage**: controle de publicação e de consumo idempotente.
- **IdempotencyRecord**: chave, hash do payload e resultado associado.
- **ProviderProcess**: processo no provider, com id externo, status normalizado e metadata nativa.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% das repetições de criação com mesma chave e mesmo payload retornam o mesmo processo, inclusive sob 50 requisições concorrentes (exatamente 1 processo criado).
- **SC-002**: Um processo criado chega a `COMPLETED` com o provider simulado em até 30 segundos em ambiente local, sem intervenção manual.
- **SC-003**: Nenhum processo confirmado pela API é perdido ao reiniciar broker ou worker durante o fluxo; ao restabelecer, todos concluem.
- **SC-004**: Entrega duplicada de qualquer mensagem produz zero efeitos duplicados (zero operações ou eventos repetidos).
- **SC-005**: 100% das transições de estado são registradas no journal, e nenhuma transição inválida é aceita.
- **SC-006**: O ambiente completo sobe com um comando e as consultas respondem em até 1 segundo a 20 requisições por segundo em máquina de desenvolvimento.

## Assumptions

- Autenticação/autorização (OIDC, RBAC) fica para a spec 008; nesta spec os endpoints são abertos em ambiente local.
- O documento é apenas referenciado (URL) e a operação de armazenamento é simulada; armazenamento real e hash são da spec 002.
- Retry com backoff, classificação de erros e DLQ completos são da spec 003; aqui, falhas marcam a operação como falha sem reagendamento.
- Callbacks, identity proofing real e reconciliação são específicos de specs posteriores; a validação de identidade nesta spec é apenas registrada (capacidades solicitadas) e etapa `VALIDATING` é percorrida de forma simulada.
- Um único provider de assinatura simulado existe no MVP; o roteamento multi-provider é Fase 2.
- Consumidores da API são sistemas corporativos confiáveis; sem multi-tenant nesta spec.
