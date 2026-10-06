# Feature Specification: Providers reais em sandbox (DocuSign e Lacuna Signer)

**Feature Branch**: `011-real-providers-sandbox`

**Created**: 2026-10-06

**Status**: Implemented

**Input**: Adapters para DocuSign (ambiente demo) e Lacuna Signer (ambiente demo) atrás da interface de provider existente, com seleção por configuração, recebimento de webhooks, download do documento assinado e das evidências e reconciliação. Credenciais só de variáveis de ambiente (arquivo `.env` fora do git, `.env.example` documentado). Testes contra os sandboxes opcionais, pulados sem credenciais. O provider simulado continua sendo o padrão.

## Clarifications

### Session 2026-10-06

- Q: Como o provider é escolhido? → A: `Provider:Default` (`Simulated` padrão, `DocuSign`, `Lacuna`; env `Provider__Default`). A escolha vale na criação do processo no provider (`PROVIDER_CREATE_PROCESS`); depois disso o `provider_process.provider_code` manda: um `ProviderRouter` (que implementa `IProviderAdapter`) encaminha cada chamada ao adapter dono do processo. Trocar a configuração nunca quebra processos em andamento. Consumidores seguem sem poder escolher fornecedor (validador intacto).
- Q: De onde vêm as credenciais? → A: Só de variáveis de ambiente. Um carregador `.env` (raiz do repositório ou diretório atual, sem sobrescrever variáveis já definidas) alimenta API, Worker e testes; `.env` está no `.gitignore`; `.env.example` documenta cada variável. O compose repassa as variáveis ao api/worker. Nenhuma credencial em `appsettings`, logs, eventos ou respostas. Adapter selecionado sem credenciais completas falha na inicialização do processo com erro permanente claro (sem expor valores).
- Q: Como DocuSign autentica? → A: JWT Grant (chave de integração, usuário, chave privada RSA) contra `account-d.docusign.com`, token em cache até perto de expirar. Variáveis: `DOCUSIGN_INTEGRATION_KEY`, `DOCUSIGN_USER_ID`, `DOCUSIGN_ACCOUNT_ID`, `DOCUSIGN_PRIVATE_KEY` (PEM, aceita `\n` literal) ou `DOCUSIGN_PRIVATE_KEY_FILE`, `DOCUSIGN_BASE_URI` (padrão `https://demo.docusign.net/restapi`), `DOCUSIGN_AUTH_SERVER` (padrão `account-d.docusign.com`), `DOCUSIGN_CONNECT_HMAC_KEY`.
- Q: Como Lacuna autentica? → A: Chave de API no cabeçalho `X-Api-Key` (`LACUNA_API_KEY`, formato `app|chave`), base `LACUNA_BASE_URI` (padrão `https://signer-lac.azurewebsites.net`), segredo de webhook `LACUNA_WEBHOOK_SECRET`.
- Q: Fluxo de assinatura com confirmação e ordem em providers reais? → A: Providers reais não têm liberação por signatário (`SupportsPerSignerRelease=false`). Para eles o orquestrador envia os códigos de confirmação de todos os signatários logo após o envio planejado, e **só envia o documento ao provider quando todos os canais de todos os signatários estiverem confirmados**; a ordem sequencial é delegada ao roteamento do provider (`routingOrder` / `order`). Sem confirmação, o envio é imediato, como antes.
- Q: Webhooks? → A: `POST /v1/webhooks/docusign` (DocuSign Connect, assinatura HMAC-SHA256 em `X-DocuSign-Signature-1`, base64) e `POST /v1/webhooks/lacuna` (cabeçalho `X-Webhook-Secret` comparado em tempo constante). Rotas públicas (sem token), autenticadas só pela assinatura; assinatura inválida → 401 sem efeito. O payload **não** é fonte da verdade: é só um gatilho que identifica o processo; o estado vem de uma reconciliação imediata (`trigger=WEBHOOK`) que consulta o provider. Evento de processo desconhecido → 200 ignorado (nada vaza). Cada webhook válido gera `PROVIDER_WEBHOOK_RECEIVED` no journal.
- Q: Documento assinado e evidências? → A: DocuSign: PDF combinado (`documents/combined`) e Certificate of Completion (`documents/certificate`). Lacuna: conteúdo assinado e versão com relatório de assinaturas. Armazenados como `SIGNED_DOCUMENT` e `EVIDENCE` como já são hoje.
- Q: Reconciliação? → A: `GetStatusAsync` dos adapters normaliza o estado (envelope/documento e destinatários concluídos viram posições assinadas), então a reconciliação existente (agendada, manual e por webhook) funciona sem mudanças de regra.
- Q: Testes? → A: Servidores HTTP falsos em processo (contrato de cada API) cobrem adapters, webhooks e o fluxo ponta a ponta no Testcontainers. Testes contra sandboxes reais ficam em classes `Live*` marcadas com um atributo que pula quando as variáveis não existem. **Os mapeamentos HTTP foram escritos a partir da documentação pública e verificados apenas contra os servidores falsos; precisam ser validados uma vez contra os sandboxes reais.**

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Escolher o provider por configuração (Priority: P1)

**Independent Test**: Sem configuração, processos usam o simulado. Com `Provider__Default=DocuSign` (ou `Lacuna`) e credenciais, novos processos vão ao fornecedor; processos já criados continuam no provider de origem.

**Acceptance Scenarios**:
1. **Given** nenhuma configuração, **Then** o provider é o simulado e nenhum teste ou comando exige credenciais.
2. **Given** `Provider__Default=DocuSign` sem credenciais, **When** um processo chega ao provider, **Then** a operação falha de forma permanente com mensagem que nomeia as variáveis ausentes (sem valores).
3. **Given** processo criado no simulado e config mudada para DocuSign, **Then** ele conclui no simulado.

### User Story 2 - Assinar de ponta a ponta com DocuSign ou Lacuna (Priority: P1)

**Independent Test**: Contra o servidor falso de cada API, o processo vai de CREATED a COMPLETED; o documento assinado e as evidências ficam armazenados com hash.

**Acceptance Scenarios**:
1. **Given** processo com 2 signatários em ordem, **Then** o provider recebe nome, e-mail, ordem e o documento original.
2. **Given** envio repetido (retry), **Then** não é criado segundo envelope/documento.
3. **Given** conclusão no provider, **Then** o documento assinado e as evidências são baixados e armazenados.
4. **Given** falha 429/5xx do provider, **Then** erro transitório (retry); 4xx, permanente.
5. **Given** confirmação por código exigida, **Then** nada é enviado ao provider antes de todos confirmarem.

### User Story 3 - Receber webhooks e reconciliar (Priority: P1)

**Acceptance Scenarios**:
1. **Given** webhook assinado de conclusão, **Then** o processo é reconciliado na hora e segue para o documento final, sem esperar o polling.
2. **Given** assinatura inválida/ausente, **Then** 401 e nenhum efeito.
3. **Given** webhook que afirma "concluído" mas o provider diz "enviado", **Then** o estado não muda (o provider é a verdade).
4. **Given** divergência sem webhook, **Then** a reconciliação agendada corrige.

### User Story 4 - Credenciais seguras e testes opcionais (Priority: P2)

**Acceptance Scenarios**:
1. **Given** o repositório, **Then** `.env` é ignorado pelo git e `.env.example` descreve todas as variáveis.
2. **Given** ausência de credenciais, **Then** os testes `Live*` aparecem como pulados, não como falha.
3. **Given** qualquer fluxo, **Then** segredos não aparecem em logs, eventos, erros ou respostas.

## Requirements

- **FR-001**: `Provider:Default` DEVE selecionar o provider de novos processos; padrão simulado; processos existentes seguem o provider gravado.
- **FR-002**: DEVEM existir adapters `DocuSign` e `Lacuna` implementando `IProviderAdapter` (criação idempotente, envio, status, cancelamento, documento assinado, evidências).
- **FR-003**: Credenciais SÓ de variáveis de ambiente/`.env`; `.env` ignorado pelo git; `.env.example` documentado; segredos nunca logados.
- **FR-004**: Webhooks DEVEM validar assinatura, usar o payload apenas como gatilho e reconciliar contra o provider.
- **FR-005**: Providers sem liberação por signatário DEVEM só receber o documento após todas as confirmações.
- **FR-006**: Erros HTTP DEVEM ser classificados (429/5xx transitórios, demais 4xx permanentes).
- **FR-007**: Testes contra sandboxes DEVEM ser opcionais e pulados sem credenciais.
- **FR-008**: O provider simulado e todo o comportamento existente DEVEM permanecer inalterados por padrão.

## Key Entities

- **ProviderRouter**: escolhe o adapter na criação e roteia o resto por `provider_code`.
- **DocuSignAdapter / LacunaAdapter**: tradução entre o contrato de provider e cada API.
- **ProviderWebhookHandler**: valida a assinatura e extrai a referência do provider.

## Success Criteria

- **SC-001**: Com as credenciais, trocar de provider exige apenas mudar `Provider__Default`.
- **SC-002**: Sem credenciais, toda a suíte padrão passa; os testes `Live*` ficam pulados.
- **SC-003**: Um webhook válido conclui o processo em segundos, sem depender do intervalo de polling.

## Assumptions

- Assinatura avançada/qualificada específica de cada fornecedor (certificados ICP-Brasil, etc.) não é configurada: o `signatureType` é registrado, mas ambos os adapters usam o fluxo padrão de assinatura do sandbox.
- Cada signatário precisa de e-mail nos providers reais: o adapter falha de forma permanente apontando o signatário (o validador de criação não depende do provider, que pode mudar depois).
- Idempotência dos adapters: DocuSign procura o envelope pelo campo personalizado `orchestratorRef` antes de criar; a Lacuna não tem busca equivalente conhecida, então uma queda entre a criação remota e o commit local pode duplicar o documento remoto (janela estreita, documentada).
