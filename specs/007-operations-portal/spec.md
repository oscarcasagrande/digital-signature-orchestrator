# Feature Specification: Portal Operacional

**Feature Branch**: `007-operations-portal`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Portal operacional em React (PRD seções 34 a 36): lista de processos, detalhe com timeline e abas, operações manuais com auditoria, página de dead letters e os endpoints de operação necessários."

## Clarifications

### Session 2026-10-06

- Q: Como o nome do documento aparece na lista sem consultar o JSON da requisição? → A: Nova coluna `document_file_name` em `signature_process`, preenchida na criação e retroalimentada por migration a partir do JSON existente.
- Q: Como o ator `OPERATOR` é propagado aos handlers? → A: Um `ActorContext` com escopo de requisição preenchido por middleware a partir de `X-Operator-Id` (válido: letras, dígitos e `._@:-`, até 100 caracteres; inválido → 400); os handlers disparados pela API usam esse ator em vez do `CONSUMER/api` fixo; workers mantêm os atores de sistema.
- Q: Qual a navegação e a estrutura de rotas do portal? → A: Rotas `/` (lista), `/processes/:id` (detalhe, aba na query `?tab=`), `/dead-letters`, com navegação de topo e campo de operador; sem biblioteca de componentes visuais (CSS próprio, tema claro/escuro por `prefers-color-scheme`).
- Q: Como o portal chama a API em desenvolvimento e em produção? → A: Caminhos relativos `/v1/...`; em desenvolvimento o servidor do Vite faz proxy para a API local; no compose o nginx serve os arquivos estáticos e faz proxy de `/v1` para o serviço `api` (mesma origem, sem CORS).
- Q: Como é o download de artefato pelo portal? → A: O portal chama `POST .../download-link` com o `artifactId` e abre a URL assinada devolvida; a URL base pública do link aponta para a origem do portal (`Artifacts__PublicBaseUrl` do compose), de modo que o download passe pelo proxy.
- Q: Quais operações ficam desabilitadas e quando? → A: Cancel e Reconcile exigem processo não terminal; Retry/Reprocess exigem operação em `FAILED`, `DLQ` ou `RETRY_PENDING` (e, para processo terminal, só callbacks); Download exige artefato existente; Inspect e leituras ficam sempre disponíveis.
- Q: Como testar o portal automaticamente? → A: Testes de componentes e de páginas com Vitest e Testing Library usando um servidor de API simulado (fetch mockado) cobrindo lista, detalhe, abas e operações manuais, mais testes de integração do backend novo e verificação de que o nginx serve o portal e faz proxy no smoke test.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Acompanhar todos os processos em uma lista (Priority: P1)

Um operador abre o portal e vê a lista de processos com documento, provider, assinantes assinados/total, status de negócio e operacional, data de criação e indicador de SLA, podendo filtrar, buscar e paginar.

**Why this priority**: Critério de aceite 15 e tela inicial do PRD seção 34.

**Independent Test**: Criar processos em estados distintos e verificar na lista as colunas, os filtros por status, a busca por identificador/`externalId`, a paginação e o indicador de SLA.

**Acceptance Scenarios**:

1. **Given** processos existentes, **When** o operador abre a tela inicial, **Then** vê, do mais recente ao mais antigo, uma linha por processo com processo, documento, provider, assinantes `assinados/total`, status de negócio, status operacional, criado e SLA (`OK` ou `ALERT`).
2. **Given** a lista, **When** o operador filtra por status de negócio ou operacional, **Then** só os processos correspondentes aparecem e o total reflete o filtro.
3. **Given** o campo de busca, **When** digita parte do identificador do processo ou do `externalId`, **Then** a lista é restringida aos processos que casam.
4. **Given** mais processos que o tamanho da página, **When** o operador navega, **Then** a paginação mostra as páginas corretas sem repetir itens.
5. **Given** processo `FAILED`/`REJECTED`/`EXPIRED`, ou operacional `DLQ`/`MANUAL_ACTION`, ou aguardando além do SLA de assinatura (padrão 60 min), **Then** o SLA é `ALERT`; nos demais casos `OK`.
6. **Given** a lista aberta, **When** o operador ativa a atualização automática, **Then** a lista se atualiza periodicamente sem perder filtros.
7. **Given** nenhum processo, **Then** o portal mostra um estado vazio claro; **Given** a API indisponível, **Then** mostra uma mensagem de erro com opção de tentar novamente.

---

### User Story 2 - Entender um processo: timeline e abas (Priority: P1)

Ao abrir um processo, o operador vê o cabeçalho com status, uma timeline legível dos acontecimentos e as abas Overview, Signers, Identity Proofing, Operations, Artifacts, Provider Metadata, Audit Trail, Callbacks e Errors.

**Why this priority**: PRD seção 35.

**Independent Test**: Abrir um processo concluído e um processo com falha e verificar timeline e cada aba com os dados correspondentes.

**Acceptance Scenarios**:

1. **Given** um processo, **When** o operador o abre, **Then** vê identificador, `externalId`, documento, provider, estados de negócio e operacional, datas e SLA.
2. **Given** o journal do processo, **Then** a timeline lista os eventos em ordem cronológica com horário e descrição legível (por exemplo "Process created", "Original document stored", "Sent to provider", "Signed", "Callback delivered").
3. **Given** as abas, **Then** Overview resume o processo; Signers lista signatários (documento mascarado) e se assinaram; Identity Proofing mostra as validações de identidade solicitadas; Operations lista operações com tipo, status, tentativas, próxima tentativa e erro; Artifacts lista artefatos com tipo, tamanho e hash; Provider Metadata mostra os metadados do provider; Audit Trail mostra todos os eventos com ator, `correlationId` e `causationId`; Callbacks mostra as entregas; Errors reúne operações com falha/retry/DLQ, dead letters do processo e seus motivos.
4. **Given** um processo inexistente, **Then** o portal mostra "não encontrado".
5. **Given** o portal em larguras de tela estreitas, **Then** tabelas rolam horizontalmente e a navegação continua utilizável.

---

### User Story 3 - Executar operações manuais com auditoria (Priority: P1)

O operador executa, a partir do detalhe, as operações manuais do PRD seção 36; cada uma exige confirmação, mostra o resultado e fica registrada na trilha de auditoria com a identidade do operador.

**Why this priority**: PRD seção 36: "Alterações manuais deverão sempre gerar eventos de auditoria".

**Independent Test**: Executar cada operação manual pelo portal e verificar o efeito, o resultado exibido e o evento de auditoria com o operador.

**Acceptance Scenarios**:

1. **Given** uma operação em `FAILED`, `DLQ` ou `RETRY_PENDING`, **When** o operador escolhe "Retry Operation"/"Reprocess From Operation" e confirma (com motivo opcional), **Then** a operação é reprocessada e o evento `OPERATION_REPROCESS_REQUESTED` registra o operador.
2. **Given** uma entrega de callback com falha, **When** escolhe "Retry Callback", **Then** o envio é refeito (mesmo com o processo terminal) e auditado.
3. **Given** um processo não terminal, **When** escolhe "Reconcile Provider", **Then** vê o resultado (corrigido, consistente, não aplicável ou erro do provider) e o evento `RECONCILIATION_REQUESTED` registra o operador (e `RECONCILIATION_CORRECTED` se corrigiu).
4. **Given** um artefato, **When** escolhe "Download Artifact", **Then** o portal obtém um link temporário assinado e inicia o download; a emissão fica auditada (`DOWNLOAD_LINK_ISSUED`) com o operador.
5. **Given** um processo não terminal, **When** escolhe "Cancel Process" e confirma, **Then** o processo é cancelado e auditado com o operador.
6. **Given** a aba Provider Metadata, **When** o operador escolhe "Inspect Provider Metadata", **Then** vê os metadados nativos do provider e o acesso é auditado (`PROVIDER_METADATA_INSPECTED`) com o operador.
7. **Given** operações indisponíveis para o estado atual (ex.: cancelar processo terminal), **Then** o portal as desabilita explicando o motivo.
8. **Given** o operador não informou sua identidade, **Then** o portal pede um identificador de operador antes de permitir operações manuais, e o identificador é enviado em todas as chamadas.
9. **Given** uma operação rejeitada pela API (conflito, não encontrado), **Then** o portal mostra a mensagem retornada e não altera a tela de forma enganosa.

---

### User Story 4 - Ver operações em DLQ (Priority: P2)

O operador consulta, em uma página própria, as dead letters pendentes e resolvidas por domínio e navega para o processo.

**Why this priority**: Critério de aceite 14 e visibilidade operacional.

**Independent Test**: Provocar uma dead letter, abrir a página e filtrar por domínio; navegar ao processo.

**Acceptance Scenarios**:

1. **Given** dead letters, **When** o operador abre a página, **Then** vê domínio, processo, operação, tipo de erro, motivo, tentativas e datas.
2. **Given** filtros de domínio e de pendentes/resolvidas, **Then** a lista reflete os filtros e a paginação.
3. **Given** uma linha, **When** o operador a seleciona, **Then** navega para o detalhe do processo (aba Errors).

---

### Edge Cases

- Muitos eventos no journal: a timeline e o Audit Trail paginam ou limitam a exibição com opção de carregar mais.
- Operador com sessão aberta e dados que mudam: um botão de atualizar recarrega o detalhe; ações usam o estado mais recente devolvido pela API.
- Operação manual executada duas vezes em sequência rápida: a segunda recebe conflito da API e o portal mostra a mensagem.
- Dados pessoais: o portal só exibe o que a API devolve (CPF mascarado); nenhum conteúdo de artefato é exibido, apenas metadados e download por link temporário.
- Identificador de operador contendo caracteres inválidos: é sanitizado/limitado (máx. 100 caracteres).
- Respostas muito grandes de metadados do provider: exibidas em visualizador de JSON com rolagem.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A API MUST expor `GET /v1/signature-processes` com filtros `status`, `operationalStatus` e `q` (parte do identificador ou do `externalId`, sem distinção de maiúsculas), paginação (`page`, `pageSize`, máximo 200), ordenação por criação decrescente e, por item: `processId`, `externalId`, `documentFileName`, `provider`, `signersSigned`, `signersTotal`, `businessStatus`, `operationalStatus`, `createdAt`, `updatedAt` e `sla`.
- **FR-002**: O SLA MUST ser `ALERT` quando o processo está `FAILED`, `REJECTED` ou `EXPIRED`, ou com estado operacional `DLQ` ou `MANUAL_ACTION`, ou não terminal há mais que o limite configurável (padrão 60 minutos); caso contrário `OK`.
- **FR-003**: O detalhe do processo (`GET /v1/signature-processes/{id}`) MUST incluir `documentFileName`, `provider` e `sla`.
- **FR-004**: A API MUST expor `GET /v1/signature-processes/{id}/provider` com os metadados do provider (código, id no provider, referência, status normalizado, metadados nativos, atualização) e MUST registrar o acesso no journal (`PROVIDER_METADATA_INSPECTED`) com o ator.
- **FR-005**: `GET /v1/dead-letters` MUST aceitar o filtro `processId`.
- **FR-006**: A API MUST identificar o autor das ações manuais pelo cabeçalho opcional `X-Operator-Id` (máx. 100 caracteres, caracteres seguros), registrando o ator como `OPERATOR` com esse identificador nos eventos de journal; sem o cabeçalho o ator permanece `CONSUMER`/`api`.
- **FR-007**: Toda operação manual exposta no portal MUST gerar evento de journal com o ator: reprocesso (`OPERATION_REPROCESS_REQUESTED`), reconciliação (`RECONCILIATION_REQUESTED`, também para resultado consistente), emissão de link de download (`DOWNLOAD_LINK_ISSUED`), cancelamento (`PROCESS_CANCELLED`) e inspeção de metadados (`PROVIDER_METADATA_INSPECTED`).
- **FR-008**: O portal MUST ser uma aplicação React com Vite e TypeScript, servida por nginx no docker compose com proxy das chamadas `/v1` para a API (mesma origem).
- **FR-009**: O portal MUST oferecer a tela inicial com lista, filtros, busca, paginação, atualização automática opcional e estados vazio/erro/carregando.
- **FR-010**: O portal MUST oferecer a página de detalhe com cabeçalho, timeline legível e as abas Overview, Signers, Identity Proofing, Operations, Artifacts, Provider Metadata, Audit Trail, Callbacks e Errors.
- **FR-011**: O portal MUST oferecer as operações manuais Retry Operation, Retry Callback, Reprocess From Operation, Reconcile Provider, Download Artifact, Cancel Process e Inspect Provider Metadata, cada uma com confirmação (e motivo opcional quando aplicável), exibição do resultado e do erro e desabilitação conforme o estado.
- **FR-012**: O portal MUST exigir e persistir localmente um identificador de operador e enviá-lo em `X-Operator-Id` em todas as chamadas.
- **FR-013**: O portal MUST oferecer a página de dead letters com filtros de domínio e de pendentes/resolvidas, paginação e navegação ao processo.
- **FR-014**: O portal MUST NOT exibir conteúdo de artefatos nem dados pessoais além do que a API devolve mascarado, e MUST tratar falhas da API com mensagem e nova tentativa.
- **FR-015**: A interface MUST seguir práticas básicas de acessibilidade (rótulos, foco visível, contraste, navegação por teclado) e funcionar em larguras estreitas.

### Key Entities

- **ProcessListItem**: linha da lista de processos (com SLA).
- **ProviderInfo**: metadados do provider de um processo.
- **Operador**: identidade de quem executa a ação manual, registrada como ator do journal.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Um operador localiza um processo por identificador ou `externalId` e abre seu detalhe em até 3 interações.
- **SC-002**: 100% das operações manuais disponíveis no portal geram um evento de auditoria com o identificador do operador.
- **SC-003**: A lista de 50 processos e o detalhe de um processo carregam e exibem os dados em até 2 segundos em ambiente local.
- **SC-004**: 100% das abas do PRD seção 35 existem e exibem dados reais de um processo concluído e de um com falha.
- **SC-005**: Nenhuma tela exibe CPF completo nem conteúdo de artefato.
- **SC-006**: O fluxo completo (listar, abrir, reconciliar, baixar artefato, cancelar, reprocessar) é coberto por testes automatizados do portal e da API.

## Assumptions

- Autenticação OIDC e papéis (RBAC) do portal são entregues na spec 008; aqui o operador se identifica por um identificador local não confiável, adequado apenas a ambientes de desenvolvimento.
- SLA aqui é um indicador simples; gestão de SLA e dashboard são Fase 2.
- O provider exibido é o código do adapter ativo (simulado) por processo, não um nome de fornecedor.
- O portal consome apenas a API pública `/v1`; não acessa banco nem filas.
- Idioma da interface: inglês para rótulos do PRD (Overview, Signers…), com mensagens curtas em inglês.
