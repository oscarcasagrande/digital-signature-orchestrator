# Feature Specification: Resiliência, Retry, DLQ e Reprocessamento

**Feature Branch**: `003-resilience`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Resiliência (PRD seções 14 a 17): retry automático com backoff exponencial e jitter, classificação de erros, DLQ por domínio, reprocessamento a partir da operação que falhou."

## Clarifications

### Session 2026-10-06

- Q: Como o jitter é aplicado e qual a base de cálculo? → A: Espera = valor do cronograma da tentativa (lista configurável por tipo; se a lista estiver vazia usa-se base × multiplicador^(n-1) limitado por um máximo) multiplicado por um fator aleatório uniforme entre 0,8 e 1,2.
- Q: O que acontece com o orçamento de tentativas no reprocessamento manual? → A: O contador `attempt` volta a zero para a operação reprocessada e `maxAttempts` mantém o valor da política; a falha anterior fica registrada no journal.
- Q: Quais erros de origem de documento HTTP são permanentes? → A: 4xx exceto 408 e 429 (ex.: 404, 403, 410) são permanentes; 408, 429 e 5xx, timeout e falha de conexão são transitórios.
- Q: Como filas de domínio são mapeadas para operações? → A: `DOCUMENT_DOWNLOAD`, `DOCUMENT_STORE`, `SIGNED_DOCUMENT_DOWNLOAD`, `SIGNED_DOCUMENT_STORE` → `artifact`; `PROVIDER_*` → `signature-provider`; `IDENTITY_VALIDATION` → `identity-proofing`; `CALLBACK_SEND` e `OUTPUT_DELIVERY` → `callback`. O Worker consome `signature-provider` e `artifact` nesta spec.
- Q: A DLQ é a fila do broker ou um registro no banco? → A: Ambos: a entrada de dead letter no banco é a fonte de consulta e resolução; a mensagem de referência na fila `*-dlq` serve a integrações e alertas, publicada via outbox.
- Q: Como a falha de infraestrutura (ex.: banco fora) é tratada? → A: Não é falha de operação: a mensagem retorna à fila (nack com requeue) e a operação não consome tentativa.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Recuperação automática de falhas transitórias (Priority: P1)

Quando uma operação falha por motivo transitório (timeout, indisponibilidade temporária de provider ou de origem, limite de taxa, falha de rede), a plataforma reagenda automaticamente a mesma operação com espera crescente e variação aleatória, sem intervenção humana, e o processo segue até o fim quando a causa desaparece.

**Why this priority**: Critério de aceite 13 e princípio V da constitution.

**Independent Test**: Criar um processo cujo provider simulado falha de forma transitória nas duas primeiras tentativas e verificar que a operação é reagendada duas vezes e o processo chega a `COMPLETED`.

**Acceptance Scenarios**:

1. **Given** uma operação que falha com erro transitório na tentativa 1, **When** o erro é tratado, **Then** a operação fica `RETRY_PENDING` com `attempt`, `maxAttempts` e `nextRetryAt` no futuro, o estado operacional do processo é `RETRY_PENDING` e o journal registra o reagendamento.
2. **Given** o horário da próxima tentativa chegou, **When** a operação é reexecutada e tem sucesso, **Then** o estado operacional volta a `READY` e o processo continua do ponto onde parou.
3. **Given** uma operação com cronograma padrão, **When** falha consecutivamente, **Then** as esperas seguem o cronograma imediata, +5 s, +30 s, +2 min, +10 min, +30 min, +2 h, +6 h, cada uma variada por jitter dentro de ±20%.
4. **Given** um cronograma configurado para um tipo específico de operação, **When** essa operação falha, **Then** usa o cronograma e o número máximo de tentativas daquele tipo.
5. **Given** uma falha de origem do documento com HTTP 408, 429 ou 5xx, **When** classificada, **Then** é transitória e reagendada.

---

### User Story 2 - Classificação de erros e DLQ (Priority: P1)

Cada falha é classificada como transitória, permanente ou desconhecida. Erros permanentes não são repetidos e pedem ação manual; erros desconhecidos têm um número limitado de tentativas; operações que esgotam as tentativas vão para a DLQ do seu domínio, com apenas referências, e ficam identificáveis por API.

**Why this priority**: Critério de aceite 14 e constitution IV/V.

**Independent Test**: Provocar erro permanente (documento 404), desconhecido e transitório persistente e verificar, respectivamente, `MANUAL_ACTION`, retries limitados seguidos de `DLQ`, e `DLQ` após esgotar tentativas, com entrada listada na API de dead letters.

**Acceptance Scenarios**:

1. **Given** um erro permanente (ex.: origem retorna 404, payload inválido), **When** tratado, **Then** a operação vai para `FAILED` sem retry e o estado operacional do processo vira `MANUAL_ACTION`.
2. **Given** um erro não classificado, **When** tratado, **Then** a operação é repetida até o limite de tentativas para erros desconhecidos (padrão 3) e, esgotado, vai para a DLQ.
3. **Given** um erro transitório que persiste além de `maxAttempts`, **When** a última tentativa falha, **Then** a operação vai para `DLQ`, o estado operacional do processo vira `DLQ`, uma entrada de dead letter é criada e uma mensagem com apenas referências (ids) é publicada na DLQ do domínio.
4. **Given** operações de domínios distintos, **When** vão para a DLQ, **Then** cada uma cai na DLQ do seu domínio (`signature-provider-dlq` para operações do provider, `artifact-dlq` para documentos, `identity-proofing-dlq`, `callback-dlq`).
5. **Given** entradas na DLQ, **When** o consumidor consulta a API de dead letters (filtrável por domínio e por pendentes), **Then** vê processo, operação, domínio, motivo, tentativas e datas.
6. **Given** uma mensagem malformada, **When** consumida, **Then** é rejeitada para a DLQ do domínio sem derrubar o consumidor.

---

### User Story 3 - Reprocessar a partir da operação que falhou (Priority: P1)

Um operador ou sistema consumidor solicita o reprocessamento de um processo; a plataforma retoma a partir da operação que falhou (ou da indicada), sem repetir operações já concluídas, e registra a ação na trilha de auditoria.

**Why this priority**: Critério de aceite 12 e PRD seção 14.

**Independent Test**: Levar uma operação à DLQ, corrigir a causa, chamar o retry e verificar que só essa operação e as seguintes executam, que operações anteriores não são repetidas e que o evento de reprocessamento está no journal.

**Acceptance Scenarios**:

1. **Given** um processo com operação em `FAILED` ou `DLQ`, **When** o retry é solicitado sem `operationId`, **Then** a operação com falha mais recente é recolocada para execução com orçamento de tentativas renovado, o estado operacional volta a `READY` e a entrada de DLQ é marcada como resolvida.
2. **Given** `operationId` informado de uma operação com falha, **When** o retry é solicitado, **Then** apenas essa operação é reprocessada.
3. **Given** operações anteriores já concluídas, **When** o reprocessamento ocorre, **Then** elas não são reexecutadas (nenhum novo artefato, nenhuma nova operação duplicada).
4. **Given** um processo em estado terminal ou sem operação reprocessável, **When** o retry é solicitado, **Then** a resposta é conflito, sem alterações.
5. **Given** `operationId` de outro processo ou inexistente, **When** solicitado, **Then** a resposta é não encontrado.
6. **Given** qualquer reprocessamento aceito, **Then** o journal registra `OPERATION_REPROCESS_REQUESTED` com ator, motivo opcional, `correlationId` e operação.

---

### Edge Cases

- Duas solicitações de retry concorrentes para a mesma operação: apenas uma é aceita, a outra recebe conflito.
- Retry manual enquanto a tentativa automática já está agendada: apenas uma execução efetiva; a outra é ignorada.
- Cancelamento enquanto a operação está `RETRY_PENDING`: o processo é cancelado e a operação não executa.
- Falha de infraestrutura (banco indisponível) durante o tratamento: a mensagem retorna à fila e não é perdida.
- `maxAttempts` igual a 1 para um tipo de operação: a primeira falha transitória já vai para a DLQ.
- Jitter nunca produz espera negativa; a primeira tentativa é imediata.
- Mensagens e entradas de DLQ nunca carregam documentos ou dados pessoais.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST reagendar automaticamente operações que falham com erro transitório, até `maxAttempts`, com espera exponencial e jitter configuráveis por tipo de operação; o padrão MUST seguir o cronograma do PRD (imediata, 5 s, 30 s, 2 min, 10 min, 30 min, 2 h, 6 h; 8 tentativas) com jitter de ±20%.
- **FR-002**: O sistema MUST classificar cada falha como `TRANSIENT`, `PERMANENT` ou `UNKNOWN`; timeout, HTTP 408/429/5xx e falhas de rede MUST ser transitórios; payload/documento/CPF inválido, HTTP 4xx (exceto 408/429), tamanho excedido e operação não permitida MUST ser permanentes; qualquer outra falha MUST ser desconhecida.
- **FR-003**: Erros permanentes MUST NOT ser repetidos: a operação vai para `FAILED` e o processo para `MANUAL_ACTION`.
- **FR-004**: Erros desconhecidos MUST ser repetidos até um limite configurável (padrão 3 tentativas) e depois enviados à DLQ.
- **FR-005**: Operações que esgotam as tentativas MUST ficar em status `DLQ`, o processo MUST ir para o estado operacional `DLQ`, uma entrada de dead letter MUST ser persistida e uma mensagem MUST ser publicada na DLQ do domínio via outbox.
- **FR-006**: Cada domínio de integração MUST ter fila de comandos e DLQ próprias: `signature-provider`/`signature-provider-dlq`, `artifact`/`artifact-dlq`, `identity-proofing`/`identity-proofing-dlq`, `callback`/`callback-dlq`; cada operação MUST ser roteada à fila do seu domínio.
- **FR-007**: Mensagens de DLQ e entradas de dead letter MUST conter apenas referências (processId, operationId, id da entrada, domínio) e classificação/motivo resumido, sem documentos ou dados pessoais.
- **FR-008**: O sistema MUST expor `GET /v1/dead-letters` com filtros `domain` e `resolved` e paginação.
- **FR-009**: O sistema MUST expor `POST /v1/signature-processes/{id}/retry` com `operationId` e `reason` opcionais, que recoloca para execução a operação alvo (padrão: a falha mais recente em `FAILED`, `DLQ` ou `RETRY_PENDING`), sem reexecutar operações concluídas, renovando seu orçamento de tentativas.
- **FR-010**: O reprocessamento MUST registrar `OPERATION_REPROCESS_REQUESTED` no journal e marcar a dead letter correspondente como resolvida; falhas e reagendamentos MUST registrar `OPERATION_RETRY_SCHEDULED`, `OPERATION_FAILED` e `OPERATION_DEAD_LETTERED`.
- **FR-011**: Reprocessar processo terminal ou operação não reprocessável MUST retornar conflito; operação de outro processo ou inexistente MUST retornar não encontrado.
- **FR-012**: A execução de operações MUST continuar idempotente: entrega duplicada, retry manual concorrente com retry automático e reprocessamento não podem duplicar efeitos (artefatos, operações, eventos).
- **FR-013**: Mensagens malformadas MUST ser rejeitadas para a DLQ do domínio; falhas de infraestrutura no tratamento MUST devolver a mensagem à fila.
- **FR-014**: O provider simulado e a origem de documento de teste MUST permitir injetar falhas transitórias (por número de tentativas), permanentes e desconhecidas, para testes automatizados e demonstração.
- **FR-015**: Cancelar um processo com operação `RETRY_PENDING` MUST impedir sua execução.

### Key Entities

- **Operation** (estendida): novos status `RETRY_PENDING` e `DLQ`; `attempt`, `maxAttempts`, `nextRetryAt`, classificação do último erro.
- **RetryPolicy**: cronograma de esperas, jitter e máximo de tentativas por tipo de operação.
- **ErrorClassification**: `TRANSIENT`, `PERMANENT`, `UNKNOWN`.
- **DeadLetterEntry**: registro de operação enviada à DLQ (domínio, motivo, tentativas, datas, resolução).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% das falhas transitórias com recuperação dentro do limite de tentativas terminam em `COMPLETED` sem intervenção manual.
- **SC-002**: 100% das operações que esgotam as tentativas aparecem na API de dead letters do domínio correto e no estado operacional `DLQ`.
- **SC-003**: 0 reexecuções de operações já concluídas após um reprocessamento.
- **SC-004**: 0 mensagens de DLQ contendo documentos ou dados pessoais.
- **SC-005**: As esperas calculadas ficam sempre entre 80% e 120% do valor-base do cronograma e a primeira tentativa é imediata.
- **SC-006**: Após correção da causa, um reprocessamento leva o processo a `COMPLETED` em até 30 segundos em ambiente local.

## Assumptions

- Autenticação/autorização e perfil de quem reprocessa (OIDC/RBAC) são da spec 008; o ator registrado é `CONSUMER`/`api` por enquanto.
- Circuit breaker e bulkhead com limites de concorrência são Fase 2; aqui há apenas filas e DLQs separadas por domínio.
- A reconciliação e os callbacks usam o mesmo mecanismo de retry quando forem implementados (specs 004 e 006).
- O cronograma e os limites vêm de configuração; os testes usam cronogramas curtos.
- Reprocessar a partir de uma operação já concluída (e invalidar as seguintes) está fora do escopo.
