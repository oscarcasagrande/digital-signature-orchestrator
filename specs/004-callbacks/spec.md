# Feature Specification: Callbacks Assinados e Seguros

**Feature Branch**: `004-callbacks`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Callbacks (PRD seções 7 e 8): envio assíncrono, assinado por HMAC-SHA256, de eventos do processo ao sistema consumidor, com callbacks pré-registrados, proteção contra SSRF, retry independente e consulta das entregas."

## Clarifications

### Session 2026-10-06

- Q: Quais estados disparam eventos e o `FAILED`/`EXPIRED`, que hoje nenhum fluxo produz? → A: A lista é fixa (SIGNATURE_IN_PROGRESS, PARTIALLY_SIGNED, SIGNED, COMPLETED, REJECTED, CANCELLED, FAILED, EXPIRED); os fluxos atuais produzem os seis primeiros e a emissão é centralizada em um único componente, de modo que FAILED/EXPIRED passam a notificar assim que existir transição para eles.
- Q: O estado operacional do processo reflete falha de callback? → A: Não. Entregas de callback têm status próprio e a operação `CALLBACK_SEND` não altera o estado operacional do processo (nem exige que ele esteja não terminal); retry, DLQ e reprocessamento usam a spec 003 sobre a própria entrega.
- Q: Qual o formato exato da assinatura? → A: `X-Signature-Signature: sha256=<hex minúsculo de HMAC-SHA256(segredo, "{timestamp}.{corpo bruto}")>`, `X-Signature-Timestamp` em segundos Unix, `X-Signature-Event-Id` igual ao `eventId`; tolerância padrão de 5 min na verificação.
- Q: Qual conteúdo vai no corpo do evento além dos campos básicos? → A: Nenhum dado pessoal; apenas identificadores, status e, em `COMPLETED`, `document {id, downloadUrl}` com link gerado a cada tentativa (TTL padrão da spec 002).
- Q: Como a política de rede é aplicada e configurada? → A: `SsrfGuard` com `Callbacks:AllowPrivateNetworks` (padrão false), `Callbacks:AllowHttp` (padrão false), `Callbacks:AllowedHosts` (lista opcional, curinga `*.dominio`); a mesma guarda protege a busca de documentos quando `Artifacts:BlockPrivateNetworks` for true (padrão false).
- Q: Como reenviar manualmente uma entrega? → A: `POST /v1/signature-processes/{id}/retry` com o `operationId` da operação `CALLBACK_SEND` (permitido também com processo terminal); sem `operationId` e com processo terminal a resposta continua sendo conflito.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Receber atualizações do processo por callback assinado (Priority: P1)

O sistema consumidor informa um destino de callback ao criar o processo e passa a receber, de forma assíncrona, um evento a cada mudança relevante de estado, com assinatura que prova a origem e a integridade do conteúdo.

**Why this priority**: Critério de aceite 7 do PRD.

**Independent Test**: Criar um processo com destino de callback, aguardar a conclusão e verificar que o receptor recebeu os eventos esperados, com assinatura válida, e que o evento de conclusão traz o documento com link temporário.

**Acceptance Scenarios**:

1. **Given** um processo com destino de callback, **When** o estado de negócio passa a `SIGNATURE_IN_PROGRESS`, `PARTIALLY_SIGNED`, `SIGNED`, `COMPLETED`, `REJECTED`, `CANCELLED`, `FAILED` ou `EXPIRED`, **Then** um evento `SIGNATURE_PROCESS.<STATUS>` é enviado ao destino.
2. **Given** um evento enviado, **Then** o corpo contém `eventId`, `eventType`, `processId`, `externalId`, `status` e `occurredAt`, e nenhum dado pessoal dos signatários.
3. **Given** o evento de conclusão, **Then** o corpo inclui `document` com `id` do documento assinado e `downloadUrl` temporária assinada válida no momento do envio.
4. **Given** qualquer envio, **Then** a requisição carrega `X-Signature-Event-Id`, `X-Signature-Timestamp` e `X-Signature-Signature`, e o receptor consegue validar a assinatura com o segredo compartilhado.
5. **Given** assinatura ou corpo adulterados, ou um timestamp fora da tolerância (replay), **When** o receptor valida, **Then** a validação falha.
6. **Given** um processo sem destino de callback, **When** muda de estado, **Then** nenhum evento é gerado.
7. **Given** o mesmo evento reenviado por retry, **Then** `eventId` e `occurredAt` são idênticos nas tentativas (o receptor pode deduplicar).

---

### User Story 2 - Retry independente e consulta das entregas (Priority: P1)

Falhas no receptor nunca afetam o processo de assinatura: o envio é reagendado de forma independente, vai para a DLQ de callbacks ao esgotar e pode ser reenviado manualmente. O consumidor e o operador consultam o histórico de entregas.

**Why this priority**: Constitution (callbacks retentados de forma independente) e PRD seção 7.

**Independent Test**: Receptor falha nas duas primeiras tentativas; o evento é entregue na terceira, o processo permanece `COMPLETED`/`READY` durante todo o tempo e a consulta mostra tentativas e entrega.

**Acceptance Scenarios**:

1. **Given** o receptor responde 5xx, 408, 429 ou fica indisponível, **When** o envio falha, **Then** é reagendado conforme a política de retry e o estado do processo (negócio e operacional) não muda.
2. **Given** o receptor responde 4xx (exceto 408/429) ou redireciona, **Then** o envio falha de forma permanente, sem retry.
3. **Given** tentativas esgotadas, **Then** a entrega vai para a DLQ `callback-dlq` (com apenas referências) e aparece em `GET /v1/dead-letters?domain=callback`.
4. **Given** uma entrega em falha permanente ou DLQ, **When** o operador solicita o retry informando a operação, **Then** o evento é reenviado mesmo que o processo já esteja em estado terminal, com auditoria.
5. **Given** um processo, **When** o consumidor consulta `GET /v1/signature-processes/{id}/callbacks`, **Then** vê cada entrega com evento, destino (sem segredos), status, tentativas, último código de resposta e erro, e datas.
6. **Given** um processo que já chegou a um estado terminal (por exemplo `COMPLETED` ou `CANCELLED`), **Then** as entregas de callback pendentes ou em retry continuam sendo executadas e o próprio evento terminal é enviado; o estado terminal do processo nunca cancela entregas de callback.

---

### User Story 3 - Callbacks pré-registrados e proteção contra SSRF (Priority: P1)

Um administrador registra destinos por `callbackId` (URL e segredo próprios). O consumidor referencia o `callbackId` em vez de uma URL. URLs dinâmicas continuam permitidas, mas são validadas para impedir que a plataforma seja usada para atacar redes internas.

**Why this priority**: PRD seção 8 e constitution (Security & Data Protection).

**Independent Test**: Registrar um callback, criá-lo por `callbackId`, receber o evento assinado com o segredo do registro; tentar URLs para localhost/IP privado/metadata e verificar a rejeição; simular DNS que resolve para IP privado e verificar o bloqueio.

**Acceptance Scenarios**:

1. **Given** um `callbackId` registrado e ativo, **When** o processo é criado com ele, **Then** os eventos vão para a URL do registro, assinados com o segredo do registro.
2. **Given** `callbackId` inexistente ou desativado, **When** o processo é criado, **Then** a criação é rejeitada com erro de validação.
3. **Given** o cadastro de um callback, **When** concluído, **Then** o segredo é exibido uma única vez; consultas posteriores nunca o retornam.
4. **Given** uma URL dinâmica não HTTPS, de `localhost`, de IP literal privado/loopback/link-local/metadata de nuvem, **When** informada na criação, **Then** é rejeitada com erro de validação.
5. **Given** um nome de domínio público que resolve (também por DNS rebinding) para IP privado no momento do envio, **When** o envio é feito, **Then** a conexão é recusada e a falha é permanente.
6. **Given** uma allowlist configurada, **When** o host do destino não consta nela, **Then** a URL é rejeitada.
7. **Given** o destino responde com redirecionamento, **Then** o redirecionamento não é seguido e a entrega falha de forma permanente.
8. **Given** o limite de taxa por destino excedido, **Then** envios excedentes são adiados (falha transitória) e não descartados.
9. **Given** um destino lento, **Then** o envio é interrompido após o timeout configurado e tratado como falha transitória.

---

### Edge Cases

- Processo cancelado com callbacks de eventos anteriores ainda pendentes: as entregas pendentes continuam (são fatos históricos) e não são canceladas; o evento `CANCELLED` também é enviado.
- Vários eventos próximos (ex.: `SIGNED` e `COMPLETED`): cada um é uma entrega independente; a ordem de chegada não é garantida e o receptor usa `occurredAt`/`eventId`.
- Destino registrado desativado depois de eventos já enfileirados: o envio falha de forma permanente com motivo claro.
- Rotação de segredo de um registro está fora de escopo (o registro é recriado com novo `callbackId`); o envio sempre usa o segredo gravado no registro no momento da tentativa.
- Resposta do receptor muito grande: o corpo da resposta nunca é lido além de um limite.
- Entrega duplicada de mensagem da fila: ignorada (inbox) sem reenviar.
- `callback.url` e `callbackId` juntos: o `callbackId` tem precedência e a URL é rejeitada como ambígua.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST gerar um evento de callback, como operação independente `CALLBACK_SEND` na fila `callback`, quando o estado de negócio de um processo com destino passa a `SIGNATURE_IN_PROGRESS`, `PARTIALLY_SIGNED`, `SIGNED`, `COMPLETED`, `REJECTED`, `CANCELLED`, `FAILED` ou `EXPIRED`.
- **FR-002**: O corpo MUST conter `eventId`, `eventType` (`SIGNATURE_PROCESS.<STATUS>`), `processId`, `externalId`, `status`, `occurredAt` e, no evento `COMPLETED`, `document {id, downloadUrl}` com link temporário gerado no instante de cada tentativa; MUST NOT conter dados pessoais.
- **FR-003**: Cada requisição MUST carregar `X-Signature-Event-Id`, `X-Signature-Timestamp` (segundos Unix) e `X-Signature-Signature` (`sha256=` + HMAC-SHA256 em hexadecimal sobre `"{timestamp}.{corpo}"`); a plataforma MUST oferecer rotina de verificação com tolerância de timestamp configurável (padrão 5 minutos) para proteção contra replay.
- **FR-004**: `eventId` e `occurredAt` MUST ser estáveis entre tentativas do mesmo evento.
- **FR-005**: O envio MUST ser retentado de forma independente do processo principal pela política da spec 003; falha de callback MUST NOT alterar estado de negócio nem operacional do processo.
- **FR-006**: Respostas 2xx MUST encerrar a entrega com sucesso; 408, 429, 5xx, timeout e falha de rede MUST ser transitórios; demais 4xx e qualquer 3xx MUST ser permanentes.
- **FR-007**: Esgotadas as tentativas, a entrega MUST ir para a `callback-dlq` e para a lista de dead letters; o retry manual MUST funcionar também para processo em estado terminal e MUST ser auditado.
- **FR-008**: O sistema MUST expor registro (`POST /v1/callbacks`), consulta (`GET /v1/callbacks`, `GET /v1/callbacks/{callbackId}`) e desativação (`DELETE /v1/callbacks/{callbackId}`) de destinos; o segredo (informado ou gerado) MUST ser retornado somente na criação.
- **FR-009**: A criação de processo MUST aceitar `callback.callbackId` (preferencial) ou `callback.url`; `callbackId` desconhecido/desativado, URL inválida pela política ou ambos informados MUST ser rejeitados com erro de validação.
- **FR-010**: URLs dinâmicas MUST ser HTTPS, não apontar para `localhost` nem para IP literal privado, loopback, link-local, multicast, CGNAT ou metadata de nuvem, e MUST obedecer a allowlist de hosts quando configurada.
- **FR-011**: No envio, o host MUST ser resolvido uma única vez, todos os IPs validados contra a mesma política e a conexão feita ao IP validado (defesa contra DNS rebinding); violação MUST ser falha permanente.
- **FR-012**: O envio MUST NOT seguir redirecionamentos, MUST aplicar timeout (padrão 10 s), limitar a leitura da resposta e aplicar limite de taxa por host de destino (padrão 10 por segundo), adiando em vez de descartar.
- **FR-013**: O sistema MUST expor `GET /v1/signature-processes/{id}/callbacks` com as entregas (evento, destino sem segredo, status, tentativas, último código, último erro, datas).
- **FR-014**: O journal MUST registrar `CALLBACK_REQUESTED` (ao criar a entrega) e `CALLBACK_DELIVERED` (ao entregar); retries, DLQ e reprocessos reutilizam os eventos da spec 003.
- **FR-015**: Desativar o egress privado MUST ser controlado por configuração (`Callbacks:AllowPrivateNetworks`, `Callbacks:AllowHttp`), com padrão seguro (bloqueado/HTTPS) e liberação explícita apenas para desenvolvimento e testes.
- **FR-016**: A busca do documento de origem (spec 002) MUST poder usar a mesma guarda de SSRF, controlada por `Artifacts:BlockPrivateNetworks`.

### Key Entities

- **CallbackRegistration**: `callbackId`, URL, segredo, ativo, descrição, datas.
- **CallbackDelivery**: uma entrega de evento — `eventId`, `eventType`, status do processo no evento, `occurredAt`, destino (callbackId ou URL), operação associada, status (`PENDING`, `RETRY_PENDING`, `DELIVERED`, `FAILED`, `DLQ`), tentativas, último código HTTP e erro, datas.
- **SsrfGuard**: política de destinos permitidos.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos eventos entregues têm assinatura válida para o segredo do destino e 0 passam na validação com corpo, timestamp ou segredo alterados.
- **SC-002**: Em 100% das falhas transitórias do receptor dentro do limite de tentativas, o evento é entregue sem intervenção e o processo nunca muda de estado por causa disso.
- **SC-003**: 0 requisições de callback chegam a IPs de loopback, privados, link-local ou de metadata em configuração padrão, inclusive via DNS que resolve para esses IPs.
- **SC-004**: 100% das entregas permanentemente falhas ou em DLQ são identificáveis por API e reenviáveis por um operador.
- **SC-005**: O callback de conclusão chega ao receptor em até 10 segundos após o processo concluir em ambiente local, com o receptor saudável.
- **SC-006**: O segredo de um destino aparece em 0 respostas de consulta e 0 logs.

## Assumptions

- O segredo de URLs dinâmicas vem da configuração da plataforma (`Callbacks:DefaultSecret`), pois não há registro; o receptor o conhece por combinação prévia. O uso de `callbackId` é o caminho recomendado.
- Segredos de registros ficam no armazenamento transacional neste MVP; em produção devem residir em um gerenciador de segredos/KMS (spec 008 e constitution).
- Autenticação e papéis para administrar registros (OIDC/RBAC) são da spec 008; a API de registro é aberta em desenvolvimento.
- Rate limit é por instância do worker (em memória), suficiente para o MVP.
- Entrega ao destino de saída de documento (`output.destination`) permanece fora de escopo.
