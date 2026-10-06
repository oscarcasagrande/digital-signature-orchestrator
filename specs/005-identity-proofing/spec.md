# Feature Specification: Identity Proofing por Capabilities

**Feature Branch**: `005-identity-proofing`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Identity proofing (PRD seções 9 e 43): sessões e validações por capability com provider simulado, evidências armazenadas com hash, resultado agregado, retry/DLQ/reprocessamento e proteção de dados biométricos; contexto separado da assinatura."

## Clarifications

### Session 2026-10-06

- Q: Como o provider simulado decide o resultado de cada capacidade? → A: Por marcadores no conteúdo das evidências (`FAKE_SPOOF` na selfie falha `LIVENESS`; `FAKE_FACE_MISMATCH` na selfie falha `FACE_MATCH`; `FAKE_FORGED` no documento falha `DOCUMENT_AUTHENTICITY`; `FAKE_NOT_OWNER` no documento falha `DOCUMENT_OWNERSHIP`; `FAKE_NO_MATCH` na selfie falha `GOVERNMENT_BIOMETRIC_MATCH`), pelo CPF terminado em `0000` (falha `PERSON_DATA`), telefone terminado em `0000` (falha `PHONE_OWNERSHIP`), e-mail com `fraud` (falha `EMAIL_OWNERSHIP`), `deviceId` com prefixo `risky-` (falha `DEVICE_RISK`); caso contrário passa com score entre 0,80 e 0,99 determinístico; falhas transitórias/permanentes/desconhecidas por prefixo do `externalId` da sessão (`SIM-FLAKY-n-`, `SIM-DOWN-`, `SIM-UNKNOWN-`, `SIM-FAIL-`).
- Q: Como `IDENTITY_RISK` agrega as demais? → A: O risco é 1 menos a média dos scores das validações anteriores (falhas contam score 0); o resultado é `PASSED` quando o risco é menor que 0,5, com `score` igual a 1 menos o risco; detalhes trazem apenas contagens.
- Q: O que acontece com uma validação com erro permanente ou em DLQ? → A: A validação fica `ERROR`, a sessão permanece não concluída (`IN_PROGRESS`, resultado `PENDING`) até o reprocessamento, que a recoloca em `PENDING`.
- Q: Como a consistência sob concorrência é garantida? → A: Toda mutação que cria ou conclui validações e fecha a sessão ocorre numa transação que trava a linha da sessão (`SELECT ... FOR UPDATE`), serializando as operações da mesma sessão.
- Q: Qual o prefixo dos identificadores e o escopo da idempotência? → A: `prf_` para sessões e `ivl_` para validações; a chave de idempotência usa o namespace `proofing:` na mesma tabela das demais, retenção de 24 h.
- Q: As evidências podem ser baixadas? → A: Não; apenas metadados e hash (`GET /v1/proofing-sessions/{id}` lista as evidências sem conteúdo) por serem dados biométricos; o conteúdo só é lido internamente pelo adapter.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Abrir uma sessão e obter o resultado das validações (Priority: P1)

Um sistema corporativo, sem relação com assinatura, abre uma sessão de proofing para uma pessoa, informando quais capacidades de identidade precisa (cada uma obrigatória ou opcional). A plataforma executa as validações de forma assíncrona e devolve um resultado agregado e por capacidade.

**Why this priority**: Identity Proofing é um bounded context próprio e reutilizável (PRD seções 9 e 52, constitution II).

**Independent Test**: Criar uma sessão com `PERSON_DATA` (sem evidência) e verificar que, sem nenhum processo de assinatura, o resultado chega a `APPROVED` com a validação `PASSED`.

**Acceptance Scenarios**:

1. **Given** uma requisição válida com `Idempotency-Key`, **When** a sessão é criada, **Then** a resposta é aceita com `sessionId` e a sessão fica `WAITING_EVIDENCE` ou `IN_PROGRESS` conforme as capacidades pedidas.
2. **Given** a mesma chave e o mesmo conteúdo, **When** reenviada, **Then** retorna a mesma sessão; com conteúdo diferente, retorna conflito.
3. **Given** capacidades sem necessidade de evidência (por exemplo `PERSON_DATA`), **When** a sessão é criada, **Then** suas validações são executadas imediatamente e concluem sem intervenção.
4. **Given** todas as validações concluídas, **When** o consumidor consulta o resultado, **Then** recebe o resultado agregado (`APPROVED` ou `REJECTED`) e, por validação, tipo, obrigatoriedade, status, score e detalhes sem dados pessoais.
5. **Given** uma capacidade desconhecida, CPF inválido, validação duplicada ou dado do sujeito exigido pela capacidade ausente (telefone, e-mail ou dispositivo), **When** a sessão é criada, **Then** a criação é rejeitada com erro de validação por campo.
6. **Given** nenhuma menção a fornecedor, **Then** o contrato aceita apenas capacidades (campos `provider`/`providers` são rejeitados).

---

### User Story 2 - Enviar evidências e executar validações que dependem delas (Priority: P1)

O consumidor envia frente/verso do documento e a selfie. Cada evidência é guardada internamente com hash e, assim que os pré-requisitos de uma capacidade são atendidos, sua validação é executada.

**Why this priority**: Evidências são o núcleo de capacidades como `FACE_MATCH`, `LIVENESS` e `DOCUMENT_AUTHENTICITY`.

**Independent Test**: Criar sessão com `LIVENESS` e `FACE_MATCH`, enviar a selfie (só `LIVENESS` executa), depois a frente do documento (`FACE_MATCH` executa) e verificar o resultado.

**Acceptance Scenarios**:

1. **Given** uma sessão aguardando evidências, **When** o consumidor envia a frente do documento (`/documents`), **Then** a evidência é armazenada com SHA-256, tamanho e tipo de conteúdo e as capacidades que dependem apenas dela passam a executar.
2. **Given** a selfie enviada (`/biometrics`) e o documento já presente, **Then** `FACE_MATCH` (selfie + documento) executa.
3. **Given** uma evidência do mesmo tipo já enviada, **When** reenviada, **Then** recebe conflito e a primeira permanece.
4. **Given** tipo de conteúdo não suportado, conteúdo vazio, base64 inválido ou acima do limite (padrão 5 MB), **Then** a evidência é rejeitada com erro de validação.
5. **Given** a evidência de uma selfie que o provider simulado considera falsificada ou sem correspondência, **Then** `LIVENESS` ou `FACE_MATCH` ficam `FAILED` e, se obrigatórias, o resultado agregado é `REJECTED`.
6. **Given** capacidades opcionais que falham e todas as obrigatórias que passam, **Then** o resultado agregado é `APPROVED`.
7. **Given** `IDENTITY_RISK` solicitado, **Then** ele só executa depois que todas as demais validações terminaram e agrega seus resultados em um score de risco.

---

### User Story 3 - Resiliência, auditoria e proteção de dados biométricos (Priority: P2)

As validações usam o mesmo retry, DLQ e reprocessamento da plataforma; tudo é auditado; evidências biométricas são tratadas como dados sensíveis.

**Why this priority**: Constitution V, VI e Security & Data Protection.

**Independent Test**: Provocar falha transitória (recupera), falha permanente (erro que exige ação) e DLQ; reprocessar; excluir evidências de uma sessão concluída e verificar o journal.

**Acceptance Scenarios**:

1. **Given** falha transitória do provider na validação, **When** tratada, **Then** é reagendada com backoff e a sessão conclui quando o provider se recupera.
2. **Given** tentativas esgotadas, **Then** a validação vai para a DLQ `identity-proofing-dlq`, aparece em `GET /v1/dead-letters?domain=identity-proofing` e a sessão fica pendente de ação.
3. **Given** uma validação em erro ou DLQ, **When** `POST /v1/proofing-sessions/{id}/retry` é chamado (com `operationId` opcional), **Then** apenas essa validação é reexecutada e o reprocessamento é auditado.
4. **Given** qualquer sessão, **Then** o journal registra criação, validações solicitadas, evidências recebidas, resultados e conclusão, consultável por `GET /v1/proofing-sessions/{id}/events`.
5. **Given** uma sessão concluída, **When** o consumidor solicita `DELETE /v1/proofing-sessions/{id}/evidence`, **Then** objetos e registros de evidência são removidos (hashes registrados no journal) e o resultado permanece consultável; em sessão não concluída a resposta é conflito.
6. **Given** a política de retenção configurada (padrão 30 dias após a conclusão), **When** uma sessão a excede, **Then** suas evidências são excluídas automaticamente com auditoria.
7. **Given** qualquer resposta ou log, **Then** CPF aparece mascarado e conteúdo de evidências nunca é exposto nem registrado em log.

---

### Edge Cases

- Sessão cujas evidências nunca chegam: permanece `WAITING_EVIDENCE` (sem expiração temporal nesta spec) e o resultado agregado é `PENDING`.
- Evidências enviadas em paralelo: cada validação é criada uma única vez e a sessão é consistente.
- Duas validações terminando ao mesmo tempo: o resultado agregado reflete ambas, sem perda de atualização.
- Reprocessar uma validação que já passou: conflito (apenas `FAILED` por erro, `DLQ` ou `RETRY_PENDING` são reprocessáveis).
- Exclusão de evidências com validações ainda pendentes: rejeitada (conflito).
- A mesma pessoa em várias sessões: sessões independentes, sem compartilhamento de evidências.
- Falha ao armazenar evidência: nenhuma validação é criada e nenhum registro de evidência permanece.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST expor `POST /v1/proofing-sessions` com `Idempotency-Key` obrigatório, aceitando `externalId`, `subject` (`name`, `document` CPF, `phone`, `email`, `deviceId`) e `validations` (strings ou `{type, required}`), sem exigir ou conhecer processo de assinatura ou fornecedor.
- **FR-002**: O sistema MUST validar o pedido: capacidades conhecidas (PERSON_DATA, DOCUMENT_DATA, DOCUMENT_AUTHENTICITY, DOCUMENT_OWNERSHIP, FACE_MATCH, LIVENESS, GOVERNMENT_BIOMETRIC_MATCH, PHONE_OWNERSHIP, EMAIL_OWNERSHIP, DEVICE_RISK, IDENTITY_RISK), sem duplicatas, `required` padrão verdadeiro, CPF válido e dados do sujeito exigidos por capacidade (telefone → `PHONE_OWNERSHIP`, e-mail → `EMAIL_OWNERSHIP`, dispositivo → `DEVICE_RISK`).
- **FR-003**: Pré-requisitos de evidência MUST ser: `DOCUMENT_DATA`, `DOCUMENT_AUTHENTICITY`, `DOCUMENT_OWNERSHIP` → frente do documento; `LIVENESS`, `GOVERNMENT_BIOMETRIC_MATCH` → selfie; `FACE_MATCH` → selfie e frente do documento; `PERSON_DATA`, `PHONE_OWNERSHIP`, `EMAIL_OWNERSHIP`, `DEVICE_RISK` → nenhuma; `IDENTITY_RISK` → todas as outras validações terminadas.
- **FR-004**: O sistema MUST expor `POST /v1/proofing-sessions/{id}/documents` (frente e verso) e `/biometrics` (selfie), aceitando conteúdo base64 com tipo de conteúdo `image/jpeg` ou `image/png`, tamanho máximo configurável (padrão 5 MB), um artefato por tipo, armazenado internamente com SHA-256, tamanho e tipo de conteúdo.
- **FR-005**: Cada validação MUST ser uma operação `IDENTITY_VALIDATION` independente, roteada para a fila `identity-proofing`, executada por um adapter de provider de identidade com interface comum e neutra, com retry, DLQ e reprocessamento conforme a spec 003; um adapter simulado MUST permitir resultados determinísticos e injeção de falhas.
- **FR-006**: O sistema MUST manter o estado da validação (`WAITING_EVIDENCE`, `PENDING`, `PASSED`, `FAILED`, `ERROR`) com `score` (0 a 1) e detalhes sem dados pessoais, e o estado da sessão (`WAITING_EVIDENCE`, `IN_PROGRESS`, `COMPLETED`) com o resultado agregado (`PENDING`, `APPROVED`, `REJECTED`).
- **FR-007**: Quando todas as validações estiverem em `PASSED` ou `FAILED`, a sessão MUST ir para `COMPLETED`; o resultado MUST ser `REJECTED` se alguma validação obrigatória falhou e `APPROVED` caso contrário.
- **FR-008**: O sistema MUST expor `GET /v1/proofing-sessions/{id}` (sessão, estados, evidências recebidas sem conteúdo), `GET .../result` (resultado agregado e por validação) e `GET .../events` (journal).
- **FR-009**: O sistema MUST expor `POST /v1/proofing-sessions/{id}/retry` (com `operationId` e `reason` opcionais) para reexecutar validações em `ERROR`, `DLQ` ou `RETRY_PENDING`, com auditoria.
- **FR-010**: O sistema MUST executar, de forma consistente sob concorrência, a criação de validações, a conclusão de cada validação e o fechamento da sessão (sem validações duplicadas nem atualização perdida).
- **FR-011**: O journal MUST registrar `PROOFING_SESSION_CREATED`, `IDENTITY_VALIDATION_REQUESTED`, `EVIDENCE_RECEIVED`, `IDENTITY_VALIDATED`, `PROOFING_SESSION_COMPLETED`, `EVIDENCE_DELETED`, além dos eventos de retry/DLQ/reprocesso da spec 003.
- **FR-012**: O sistema MUST mascarar o CPF em respostas e logs e MUST NOT registrar nem expor o conteúdo das evidências.
- **FR-013**: O sistema MUST expor `DELETE /v1/proofing-sessions/{id}/evidence` para sessões concluídas, removendo objetos e registros de evidência, e MUST excluir automaticamente evidências de sessões concluídas há mais que a retenção configurada (padrão 30 dias), registrando a ação.
- **FR-014**: Mensagens e DLQ MUST conter apenas referências; o contexto de proofing MUST permanecer independente do de assinatura (nenhuma tabela ou endpoint de assinatura é necessário para usá-lo).
- **FR-015**: A criação do processo de assinatura MUST continuar apenas registrando as validações solicitadas, sem depender de sessões de proofing.

### Key Entities

- **ProofingSession**: sessão de proofing de uma pessoa, com sujeito, estado e resultado agregado.
- **IdentityValidation**: validação de uma capacidade na sessão, com obrigatoriedade, estado, score, detalhes e operação associada.
- **Evidence (Artifact)**: frente/verso do documento ou selfie, com hash, tamanho e tipo de conteúdo.
- **IdentityProofingAdapter**: porta para o provider de identidade (simulado).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% das sessões com apenas capacidades sem evidência chegam a `COMPLETED` em até 15 segundos, sem processo de assinatura.
- **SC-002**: 0 validações duplicadas e 0 resultados agregados incorretos sob 6 validações concorrentes na mesma sessão.
- **SC-003**: 100% das evidências armazenadas têm hash que confere com o conteúdo enviado.
- **SC-004**: 0 respostas ou logs com CPF completo ou conteúdo de evidência.
- **SC-005**: 100% das falhas transitórias dentro do limite de tentativas terminam em validação concluída sem intervenção; 100% das esgotadas são identificáveis na API de dead letters do domínio `identity-proofing`.
- **SC-006**: Após exclusão (manual ou por retenção), 0 evidências restam no armazenamento nem nos registros da sessão e o resultado continua consultável.

## Assumptions

- O provider de identidade é simulado: o resultado das validações depende de marcadores no conteúdo das evidências e do identificador externo (`SIM-` para injeção de falhas); nenhuma chamada externa ocorre.
- Evidências são enviadas em JSON com conteúdo base64 (upload multipart é uma evolução futura).
- Políticas de identity proofing (conjuntos nomeados de capacidades, PRD seção 10) e decisões baseadas em risco são Fase 2/3 e estão fora de escopo.
- Autenticação e autorização (OIDC/RBAC) e segregação por cliente são da spec 008.
- Não há expiração temporal de sessões nesta spec.
- Callback ao término da sessão fica fora de escopo; o consumidor consulta o resultado (polling).
