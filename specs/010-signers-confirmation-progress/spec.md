# Feature Specification: Signatários, confirmação e progresso

**Feature Branch**: `010-signers-confirmation-progress`

**Created**: 2026-10-06

**Status**: Implemented

**Input**: (1) API simples para signatários (name, document, email, phone, signatureType, order, confirmation, com defaults do processo); (2) confirmação por código de uso único em cada canal exigido antes de assinar; (3) progresso por processo (completedSteps, totalSteps, currentStep); (4) portal com coluna Etapas, etapas por signatário no detalhe e formulário de novo processo com upload e tabela de signatários.

## Clarifications

### Session 2026-10-06

- Q: Como o signatário informa tipo de assinatura e confirmação por padrão? → A: Objeto opcional `defaults` no processo com `signatureType` e `confirmation` (lista de canais). Precedência do tipo: `signers[i].signatureType` > `defaults.signatureType` > `signature.type` (campo atual, mantido). Confirmação: `signers[i].confirmation` > `defaults.confirmation` > nenhuma. Uma lista explícita vazia (`[]`) no signatário desliga a confirmação herdada. Se nenhum nível definir o tipo, o erro aponta `signers[i].signatureType`.
- Q: `signers[i].externalId` continua obrigatório? → A: Torna-se opcional (padrão `signer-<posição 1-based>`), para o caso comum ser só nome, CPF e contato. Quando informado, continua aceito como hoje.
- Q: Semântica de `order`? → A: Inteiro ≥ 1. Sem `order` em nenhum signatário, todos assinam em paralelo. Se algum informa, todos precisam informar (erro no signatário sem `order`). Valores iguais formam um grupo paralelo; os valores distintos precisam ser 1..k sem lacunas (o erro aponta o primeiro signatário cujo `order` ultrapassa a lacuna). Um signatário só é liberado quando todos os de `order` menor assinaram.
- Q: Quando os códigos são enviados? → A: Quando o signatário é liberado (todos de `order` menor já assinaram; sem ordem, logo após o envio do documento). A assinatura só é liberada no provider depois que todos os canais exigidos do signatário foram confirmados.
- Q: Parâmetros do código? → A: 6 dígitos numéricos, validade de 10 min, 5 tentativas erradas bloqueiam o código (reenvio gera novo código e zera tentativas), reenvio no mínimo 30 s após o último envio e no máximo 5 envios por canal (excedido: 429 com `Retry-After`). Todos configuráveis.
- Q: Quem confirma e como? → A: A aplicação consumidora (ou operador) repassa o código digitado pelo signatário por `POST /v1/signature-processes/{id}/signers/{signerId}/confirmations/{channel}/confirm`; reenvio por `.../resend`. Sem token de signatário no MVP.
- Q: O código é guardado? → A: Apenas um HMAC-SHA256 com chave de servidor; nunca em claro em banco (exceto o sink do notificador simulado), logs, eventos, outbox ou respostas. Comparação em tempo constante.
- Q: Como o envio e a confirmação viram operações? → A: Cada envio é uma operação `CONFIRMATION_SEND` assíncrona (fila, retry, DLQ no domínio `notification`). Cada tentativa de confirmação é registrada como operação `CONFIRMATION_VERIFY` concluída na requisição, com retry interno de concorrência, e gera evento no journal (`CONFIRMATION_CONFIRMED` ou `CONFIRMATION_CODE_REJECTED`/`CONFIRMATION_LOCKED`/`CONFIRMATION_CODE_EXPIRED`).
- Q: Como o progresso é planejado? → A: As etapas são determinísticas a partir do que é gravado na criação: documento recebido, uma confirmação por signatário e canal, uma assinatura por signatário, documento final e, se há callback, o callback final (entrega do evento de conclusão). Estado derivado dos fatos (artefato, confirmações, signatários, entregas). Processo cancelado/falho: etapas não concluídas viram CANCELLED (ou FAILED quando a operação correspondente falhou) e não contam.
- Q: Upload no portal? → A: `POST /v1/document-uploads` (multipart) guarda o arquivo e devolve `uploadId`; o processo referencia `document.source = {type: "UPLOAD", uploadId}`. O upload pertence ao cliente que o enviou e vale por 24 h.
- Q: Dados de contato nas respostas? → A: `email` e `phone` são mascarados nas respostas, como o CPF.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Criar processo com signatários simples (Priority: P1)

Quem integra informa só nomes e contatos; tipo de assinatura e canais de confirmação vêm de `defaults`. Pode sobrescrever por pessoa e definir ordem sequencial.

**Independent Test**: POST com `defaults` e lista mínima cria o processo com os valores herdados; payloads antigos continuam aceitos; payloads inválidos retornam erros por signatário e campo.

**Acceptance Scenarios**:
1. **Given** `defaults.signatureType=ADVANCED` e `defaults.confirmation=["EMAIL"]`, **When** crio com dois signatários sem esses campos, **Then** ambos herdam e o detalhe mostra o tipo e os canais efetivos.
2. **Given** um signatário com `confirmation: []`, **Then** ele não exige confirmação mesmo com default.
3. **Given** canal SMS sem `phone`, EMAIL sem `email`, CPF inválido ou `order` com lacuna, **When** crio, **Then** 400 (padrão de validação da API) com `signers[i].<campo>`.
4. **Given** payload da spec 001 (com `signature.type` e `externalId`), **Then** continua criando como antes, sem confirmação e em paralelo.

### User Story 2 - Confirmar por canal antes de assinar (Priority: P1)

**Independent Test**: Com o notificador simulado, o código chega ao sink; confirmar libera a assinatura; código errado, expirado ou excesso de tentativas bloqueiam; reenvio respeita rate limit.

**Acceptance Scenarios**:
1. **Given** signatário com EMAIL e SMS, **When** o documento é enviado, **Then** dois códigos são gerados e nenhuma assinatura ocorre antes de ambos serem confirmados.
2. **Given** código errado 5 vezes, **Then** o código fica bloqueado e só um reenvio o substitui.
3. **Given** código expirado, **When** confirmo, **Then** 410 e o evento é registrado.
4. **Given** reenvio antes de 30 s ou após 5 envios, **Then** 429.
5. **Given** falha transitória do notificador, **Then** o envio é retentado e o processo segue.
6. **Given** ordem sequencial, **Then** o segundo signatário só recebe códigos e assina depois do primeiro assinar.
7. **Given** qualquer fluxo, **Then** nenhum log, evento do journal, operação ou mensagem contém o código.

### User Story 3 - Acompanhar o progresso (Priority: P2)

**Independent Test**: `progress` na lista e no detalhe evolui de 0/Y até Y/Y; cancelamento e falha não contam.

**Acceptance Scenarios**:
1. **Given** processo com 2 signatários, 1 canal cada e callback, **Then** `totalSteps` = 1 + 2 + 2 + 1 + 1 = 7.
2. **Given** processo em andamento, **Then** `currentStep` é a primeira etapa não concluída, com tipo, signatário e canal.
3. **Given** processo cancelado, **Then** etapas pendentes ficam CANCELLED e não contam.
4. **Given** processo legado sem confirmação, **Then** o progresso também é calculado.

### User Story 4 - Portal (Priority: P2)

**Independent Test**: Testes do portal cobrem coluna Etapas (X/Y, barra, hover com etapa atual), lista de etapas no detalhe, formulário com upload e tabela de signatários.

**Acceptance Scenarios**:
1. **Given** a lista, **Then** a coluna Etapas mostra `X/Y`, barra com `role=progressbar` e a etapa atual no `title`.
2. **Given** o detalhe, **Then** as etapas aparecem agrupadas por signatário com o estado.
3. **Given** o formulário, **When** envio arquivo e signatários válidos, **Then** o upload é feito, o processo criado e o portal navega ao detalhe; erros da API aparecem por signatário e campo.

## Requirements

- **FR-001**: O corpo de criação DEVE aceitar `defaults` e, por signatário, `email`, `phone`, `signatureType`, `order`, `confirmation`, com a precedência das clarificações e compatibilidade com o payload atual.
- **FR-002**: A validação DEVE exigir `phone` para SMS/WHATSAPP, `email` para EMAIL, CPF válido, formatos de e-mail/telefone, canais e tipos conhecidos, `order` sem lacunas e consistente; erros DEVEM apontar `signers[i].<campo>`.
- **FR-003**: A confirmação DEVE ser um capability (`IConfirmationNotifier`) sem expor fornecedor, com notificador simulado que grava o código em um sink consultável.
- **FR-004**: O código DEVE ter expiração, limite de tentativas, reenvio com intervalo mínimo e máximo de envios; nunca em claro em logs, eventos, operações, outbox ou respostas.
- **FR-005**: A assinatura de um signatário SÓ PODE ser liberada ao provider após todos os seus canais confirmados e, com ordem, após os de `order` menor assinarem.
- **FR-006**: Cada envio e cada confirmação DEVEM ser operações; o envio com retry/DLQ; ambos geram evento no journal.
- **FR-007**: `progress` DEVE ser exposto na listagem e no detalhe, derivado das etapas planejadas, sem contar etapas canceladas ou falhas; o detalhe DEVE listar as etapas.
- **FR-008**: Upload de documento DEVE existir, segregado por cliente, e ser aceito como fonte do documento.
- **FR-009**: O portal DEVE ter coluna Etapas, etapas por signatário no detalhe e formulário de novo processo com upload e tabela de signatários.
- **FR-010**: Swagger DEVE documentar os novos campos e endpoints com exemplos; o smoke test DEVE cobrir o fluxo.

## Key Entities

- **Signer** (estendido): contato, tipo de assinatura efetivo, ordem, canais exigidos, liberação.
- **SignerConfirmation**: um por signatário e canal; estado, hash do código, validade, tentativas, envios.
- **DocumentUpload**: arquivo enviado antes da criação do processo.
- **Progress / Step**: visão derivada, não persistida.

## Success Criteria

- **SC-001**: Um processo comum é criado só com nomes, CPFs e contatos mais `defaults`.
- **SC-002**: Nenhum teste encontra o código em claro fora do sink simulado.
- **SC-003**: Em testes, nenhuma assinatura ocorre antes da confirmação de todos os canais e da vez do signatário.
- **SC-004**: `completedSteps` chega a `totalSteps` em todo processo concluído.

## Assumptions

- Sem confirmação nem ordem, o comportamento e o tempo dos processos existentes não mudam.
- O provider simulado passa a aceitar liberação explícita por signatário (capability de provider agnóstica); processos sem confirmação e sem ordem continuam no modo anterior.
- Limpeza de uploads não consumidos fica fora do escopo.
