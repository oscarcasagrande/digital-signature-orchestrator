# Feature Specification: Reconciliação Básica com o Provider

**Feature Branch**: `006-reconciliation`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Reconciliação básica (PRD seções 18 e 33): worker periódico que compara o estado interno com o do provider, corrige divergências, audita, notifica e continua o workflow; reconciliação manual e histórico."

## Clarifications

### Session 2026-10-06

- Q: O que significa "última atividade" para o limite de obsolescência? → A: A maior entre a data da última atualização do processo e a da última conferência (`last_reconciled_at`); toda conferência, mesmo sem divergência, atualiza `last_reconciled_at`, evitando reconsulta antes de um novo limite.
- Q: Como a concorrência com o fluxo normal e com várias instâncias é resolvida? → A: Pelo token otimista `version` do processo: a gravação que perde a corrida relê o processo e reavalia (até 5 vezes); não há transação com bloqueio explícito.
- Q: Quais registros de reconciliação são gravados? → A: Somente divergências corrigidas e toda chamada manual (qualquer resultado, inclusive `CONSISTENT`, `NOT_APPLICABLE` e `PROVIDER_ERROR`); conferências agendadas sem divergência ou com erro do provider não gravam registro (apenas log).
- Q: Como o histórico se relaciona com o estado e quem é o ator no journal? → A: O registro guarda gatilho (`SCHEDULED` ou `MANUAL`), estado interno anterior, status do provider, resultado e estado novo; o ator do journal é `SYSTEM/reconciliation-worker` no agendamento e `CONSUMER/api` no manual.
- Q: O que a correção faz com as operações de acompanhamento? → A: Operações `PROVIDER_STATUS_CHECK` do processo em `NOT_STARTED`, `RETRY_PENDING`, `FAILED` ou `DLQ` passam a `CANCELLED` (dead letters abertas resolvidas por `reconciliation`) e, se o processo estava em `RETRY_PENDING`, `DLQ` ou `MANUAL_ACTION`, o estado operacional volta a `READY`.
- Q: A reconciliação manual de processo terminal ou sem provider? → A: Processo terminal retorna conflito (409); processo não terminal sem registro no provider (ou fora da janela de estados) retorna `NOT_APPLICABLE`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Corrigir automaticamente divergências com o provider (Priority: P1)

Mesmo que o acompanhamento normal falhe (webhook perdido, consulta de status esgotada), um processo aguardando assinatura é periodicamente conferido contra o provider. Se o provider já concluiu, o processo é atualizado e o fluxo continua até o fim.

**Why this priority**: PRD seção 18: "Webhook + Reconciliation"; constitution V (webhooks não são a única fonte de estado).

**Independent Test**: Criar um processo cujo estado interno está em `SIGNATURE_IN_PROGRESS` há mais tempo que o limite enquanto o provider já registra a assinatura concluída, sem nenhuma operação de acompanhamento ativa; verificar que o reconciliador o leva a `COMPLETED`.

**Acceptance Scenarios**:

1. **Given** um processo `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED` sem atividade há mais que o limite de obsolescência, **When** o worker de reconciliação executa, **Then** o provider é consultado e o estado interno é comparado ao do provider.
2. **Given** o provider informa assinatura concluída, **When** a divergência é detectada, **Then** os signatários são atualizados, o processo vai a `SIGNED`, um evento de callback `SIGNED` é gerado e o fluxo continua (documento assinado e evidência são obtidos e o processo chega a `COMPLETED`).
3. **Given** o provider informa assinatura parcial, **When** o processo está `SIGNATURE_IN_PROGRESS`, **Then** vai a `PARTIALLY_SIGNED` com os signatários correspondentes e callback correspondente.
4. **Given** o provider informa rejeição ou cancelamento, **Then** o processo vai a `REJECTED` ou `CANCELLED`, com callback, e o fluxo de acompanhamento é encerrado.
5. **Given** estado interno e do provider consistentes (assinatura ainda pendente), **When** o worker confere, **Then** nada é alterado no processo além de marcar a data da última conferência.
6. **Given** operações de acompanhamento de status pendentes, em erro ou em DLQ para o processo corrigido, **Then** elas são neutralizadas (canceladas, com as dead letters resolvidas) e o estado operacional volta a `READY`.
7. **Given** o provider indisponível, **When** o worker consulta, **Then** o erro é registrado, o processo é reconferido no próximo ciclo e o worker continua com os demais.
8. **Given** o processo sem relação com o provider ainda (ex.: documento não enviado) ou em estado fora da janela, **Then** não é candidato.

---

### User Story 2 - Reconciliar sob demanda e consultar o histórico (Priority: P2)

Um operador ou sistema consumidor pede a reconciliação de um processo e consulta o histórico de conferências que encontraram divergência.

**Why this priority**: Operação manual "Reconcile Provider" (PRD seção 36) e API do PRD seção 43.

**Independent Test**: Chamar a reconciliação manual em um processo divergente e em um consistente; consultar o histórico.

**Acceptance Scenarios**:

1. **Given** um processo divergente, **When** `POST /v1/signature-processes/{id}/reconcile`, **Then** a divergência é corrigida e a resposta indica o resultado (`CORRECTED`), o estado interno anterior, o estado do provider e o novo estado.
2. **Given** um processo consistente, **Then** a resposta é `CONSISTENT` e nenhum estado muda.
3. **Given** um processo sem registro no provider, **Then** a resposta é `NOT_APPLICABLE`.
4. **Given** erro do provider na chamada manual, **Then** a resposta é `PROVIDER_ERROR` com o motivo resumido e o erro é registrado.
5. **Given** processo em estado terminal ou inexistente, **Then** a resposta é conflito ou não encontrado, respectivamente.
6. **Given** reconciliações que corrigiram, falharam no provider (manual) ou foram pedidas manualmente, **When** o consumidor consulta `GET /v1/signature-processes/{id}/reconciliations`, **Then** vê cada registro com gatilho (agendado/manual), estados comparados, resultado e data.
7. **Given** qualquer correção ou chamada manual, **Then** o journal registra a ação com o ator (worker ou API) e o `correlationId`.

---

### User Story 3 - Segurança sob concorrência e várias instâncias (Priority: P2)

A reconciliação convive com o fluxo normal e com várias instâncias do worker sem duplicar efeitos.

**Why this priority**: Constitution III (idempotência) e escala horizontal.

**Independent Test**: Executar reconciliações concorrentes do mesmo processo divergente e verificar uma única correção, uma única operação de download e um único evento de callback.

**Acceptance Scenarios**:

1. **Given** várias reconciliações simultâneas do mesmo processo, **Then** apenas uma corrige; as demais encontram o processo consistente e nada duplica.
2. **Given** o acompanhamento normal e a reconciliação concluindo ao mesmo tempo, **Then** o processo chega a `COMPLETED` uma única vez, sem operação de download duplicada nem falha por transição inválida.
3. **Given** a correção já aplicada, **When** reexecutada, **Then** nada muda (idempotente).

---

### Edge Cases

- Provider informando assinado enquanto o processo está `READY_FOR_SIGNATURE` (envio perdido): passa por `SIGNATURE_IN_PROGRESS` antes de `SIGNED`, com os eventos correspondentes.
- Provider informa um estado que o processo já ultrapassou: nada a fazer (consistente).
- Processo cancelado ou concluído durante a conferência: a gravação detecta a mudança e reavalia, sem sobrescrever.
- Muitos candidatos: processados em lotes configuráveis, mais antigos primeiro.
- Falha em um processo do lote: não interrompe os demais.
- Provider indisponível em todas as conferências agendadas: sem tempestade de registros (apenas log e nova tentativa no próximo ciclo).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST executar um worker de reconciliação independente e periódico, com intervalo, limite de obsolescência e tamanho de lote configuráveis (padrão: a cada 60 s, obsolescência 120 s, lote 50).
- **FR-002**: Candidatos MUST ser processos em `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED` com registro no provider, cuja última atividade ou última conferência seja mais antiga que o limite, do mais antigo ao mais recente.
- **FR-003**: O worker MUST consultar o provider pela interface comum do adapter e comparar status normalizado e signatários assinados com o estado interno.
- **FR-004**: Divergências MUST ser corrigidas pela máquina de estados: provider assinado → `SIGNED`; parcial → `PARTIALLY_SIGNED`; rejeitado → `REJECTED`; cancelado → `CANCELLED`; transições respeitam a máquina (passando por `SIGNATURE_IN_PROGRESS` quando necessário).
- **FR-005**: Ao corrigir para `SIGNED`, o sistema MUST criar a operação `SIGNED_DOCUMENT_DOWNLOAD` (uma única vez) e continuar o workflow até a conclusão.
- **FR-006**: Toda correção MUST gerar os eventos de callback do estado novo (spec 004) e MUST neutralizar operações `PROVIDER_STATUS_CHECK` pendentes, em erro ou em DLQ do processo (canceladas, dead letters resolvidas, estado operacional `READY`).
- **FR-007**: O sistema MUST registrar cada divergência corrigida e cada reconciliação manual em um registro de reconciliação (gatilho, estados interno e do provider, resultado, detalhes) e no journal (`RECONCILIATION_DISCREPANCY_FOUND`, `RECONCILIATION_CORRECTED`, `RECONCILIATION_PROVIDER_ERROR`).
- **FR-008**: Conferências sem divergência MUST NOT alterar o processo além da data da última conferência e MUST NOT gerar registros nem eventos no journal nas execuções agendadas.
- **FR-009**: Erros do provider na execução agendada MUST ser registrados em log e não interromper o ciclo nem os demais processos; o processo MUST ser reconferido no ciclo seguinte.
- **FR-010**: O sistema MUST expor `POST /v1/signature-processes/{id}/reconcile` com os resultados `CORRECTED`, `CONSISTENT`, `NOT_APPLICABLE` e `PROVIDER_ERROR`, rejeitando processo terminal (conflito) e inexistente (não encontrado).
- **FR-011**: O sistema MUST expor `GET /v1/signature-processes/{id}/reconciliations` com o histórico.
- **FR-012**: A reconciliação MUST ser idempotente e segura sob concorrência com o fluxo normal e com várias instâncias, sem duplicar operações, eventos ou callbacks.
- **FR-013**: O estado interno MUST NOT ser alterado quando o provider estiver atrás do estado interno.

### Key Entities

- **ReconciliationRecord**: resultado de uma conferência relevante (divergência corrigida ou pedido manual) com gatilho, estados comparados, resultado e detalhes.
- **Candidato à reconciliação**: processo aguardando o provider cuja última atividade/conferência é mais antiga que o limite.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos processos aguardando assinatura cujo provider já concluiu, e sem acompanhamento ativo, chegam a `COMPLETED` sem intervenção em até 2 ciclos do worker após ultrapassar o limite.
- **SC-002**: Sob 10 reconciliações concorrentes do mesmo processo divergente, exatamente 1 correção, 1 operação de download e 1 evento de callback por estado novo.
- **SC-003**: 0 alterações de estado, registros ou eventos em conferências agendadas sem divergência.
- **SC-004**: 100% das correções e reconciliações manuais têm registro e evento de journal com estados antes/depois.
- **SC-005**: Uma falha do provider em um processo não impede a conferência dos demais do mesmo ciclo (100% dos demais candidatos são processados).
- **SC-006**: A contagem de divergências corrigidas é consultável (base da métrica Reconciliation Rate da spec 008).

## Assumptions

- O provider é o simulado; a reconciliação usa a consulta de status do adapter, que é a mesma usada pelo acompanhamento normal.
- Webhooks do provider não existem neste MVP; a reconciliação (junto do acompanhamento por consulta) é a segunda fonte de estado.
- Métricas e alertas de divergência são entregues na spec 008; aqui ficam registros consultáveis.
- Identity proofing não é reconciliado (não há provider externo real).
- Autenticação/autorização da operação manual (OIDC/RBAC) é da spec 008.
