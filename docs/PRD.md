# PRD — Plataforma de Orquestração de Assinaturas Digitais e Identity Proofing

**Versão:** 2.0  
**Status:** Em vigor — alinhado à solução implementada (MVP entregue; Fases 2 e 3 parcialmente entregues ou planejadas)  
**Tipo de Produto:** Plataforma Corporativa / API / Backoffice  
**Domínio:** Assinatura Eletrônica, Identidade Digital e Gestão de Documentos  
**Data:** Outubro/2026 (revisão 2.0 de 06/10/2026)  
**Detalhe técnico:** [ARCHITECTURE.md](ARCHITECTURE.md)

---

## Como ler esta versão

Esta versão descreve a solução **como ela existe hoje**. Onde o texto original divergia do que foi implementado, vale o implementado; o que o código não faz está marcado como parcial ou planejado, nunca descrito como entregue.

A numeração das seções 1 a 52 é a da versão 1.0 e foi preservada, porque specs, decisões e comentários de código referenciam seções por número. O que é novo foi acrescentado ao final (seções 53 a 55), sem renumerar nada.

Fontes da verdade, em ordem: código em `src` e `portal`; OpenAPI gerado pela API (`/swagger`); `specs/001` a `specs/011`; `docs/DECISIONS.md`; `docs/PROGRESS.md`; `.specify/memory/constitution.md`; `README.md`.

### Legenda de estado

| Estado | Significado |
|---|---|
| **Implementado** | Existe no código, com comportamento descrito aqui e teste automatizado. |
| **Implementado parcialmente** | Existe, mas com lacuna declarada no próprio item (o que falta está dito). |
| **Planejado** | Não existe no código; permanece como requisito de fase futura. |

Cada seção de requisito traz um bloco `Estado` com a referência às specs e às decisões (`D-xxx`, em [DECISIONS.md](DECISIONS.md)). A tabela completa está na seção 53.

### Histórico de alterações

| Versão | Data | Alteração |
|---|---|---|
| 1.0 | Outubro/2026 | Versão original (Draft): visão, princípios, contratos conceituais, MVP, Fases 2 e 3. |
| 2.0 | 06/10/2026 | Alinhamento à solução implementada (specs 001 a 011 e correções posteriores). Resumo abaixo. |

**O que mudou da 1.0 para a 2.0**

- **Estado por seção**: cada requisito passou a indicar implementado, parcial ou planejado, com specs e decisões de origem; rastreabilidade na seção 53.
- **Contratos reais**: endpoints, payloads, códigos de erro, máquinas de estado, eventos do journal, tipos de artefato, capabilities e papéis de acesso foram substituídos pelos reais (seções 6 a 17, 24, 26, 36, 37, 43).
- **Signatários completos**: nome, CPF, e-mail e telefone; tipo de assinatura (`SIMPLE`, `ADVANCED`, `QUALIFIED`) por pessoa ou por `defaults`; ordem de assinatura com grupos paralelos; canais de confirmação (`EMAIL`, `SMS`, `WHATSAPP`) (seção 6, spec 010).
- **Código de confirmação** com validade, limite de tentativas, bloqueio, reenvio com intervalo e teto de envios; só o hash é guardado (seção 6, spec 010).
- **Progresso em etapas X/Y** na lista e no detalhe do processo (seções 34 e 35, spec 010).
- **Criação de processo pelo portal** por `operator` e `admin`, com upload de documento (seções 34 e 37, spec 010 e correção posterior).
- **Segregação por cliente** estendida a processos, sessões de proofing, callbacks registrados, dead letters, uploads e artefatos (seção 37, specs 008 e 009).
- **Login com PKCE** (authorization code + S256) no portal e **renovação do access token** por refresh token (seção 37, spec 009 e correção posterior).
- **Adapters de DocuSign e Lacuna Signer em sandbox**, escolhidos por configuração, com webhooks assinados (seções 18, 29 e 47, spec 011). Os mapeamentos HTTP ainda não foram validados contra os sandboxes reais.
- **Limites e rate limit** documentados: limite na criação de processo, no reenvio de código, em callbacks por host, no tamanho de documentos e evidências (seções 8, 37 e 42).
- **Correções de texto do PRD original** onde ele divergia do que foi construído: `output.destination` (não implementado), abas do portal (dez, não nove), `FAILED` e `EXPIRED` (sem gatilho), circuit breaker e bulkhead (não implementados), políticas de identity proofing (não implementadas), single-use de download (não implementado).
- **Seções novas**: 53 (Rastreabilidade), 54 (Limitações Conhecidas e Riscos) e 55 (Documentos Relacionados).
- **Critérios de aceite e Definition of Done** (seções 49 e 50) agora indicam o teste automatizado que cobre cada item, ou a ausência dele.
- **Detalhe técnico** (projetos, modelo de dados, filas, configuração, docker-compose) saiu do escopo do PRD e está em [ARCHITECTURE.md](ARCHITECTURE.md).

---

# 1. Visão do Produto

A Plataforma de Orquestração de Assinaturas Digitais tem como objetivo fornecer uma camada corporativa centralizada para gerenciamento do ciclo de vida completo de documentos que necessitam de assinatura eletrônica ou digital.

A plataforma deverá abstrair provedores externos como DocuSign, Clicksign, Adobe Sign, D4Sign, serviços de Identity Proofing e outros fornecedores, expondo aos sistemas consumidores uma interface única, independente de fornecedor.

A solução será responsável por:

- receber e preservar documentos;
- executar validações de identidade;
- selecionar e integrar provedores de assinatura;
- controlar todo o workflow de assinatura;
- armazenar artefatos e evidências;
- manter uma máquina de estados;
- registrar integralmente as operações realizadas;
- realizar callbacks para sistemas consumidores;
- disponibilizar documentos assinados de maneira segura;
- garantir resiliência e reprocessamento;
- oferecer interface operacional para acompanhamento e suporte.

A plataforma deverá atuar como **System of Record do processo de assinatura**.

Os provedores externos serão considerados executores de capacidades específicas da plataforma, não proprietários do processo.

> **Estado atual da visão**
>
> | Item da visão | Estado | Observação |
> |---|---|---|
> | Interface única, independente de fornecedor | Implementado | Contrato sem campos de fornecedor; `provider`, `providers`, `providerId` e `vendor` são rejeitados com 400. |
> | Provedores de assinatura | Implementado parcialmente | Provider simulado (padrão), DocuSign e Lacuna Signer em sandbox. Clicksign, Adobe Sign e D4Sign: planejado. DocuSign e Lacuna só foram exercitados contra servidores falsos (seção 54). |
> | Identity Proofing | Implementado parcialmente | Sessões e 11 capabilities com adapter simulado; nenhum fornecedor real de identidade. |
> | System of Record | Implementado | Original, assinado e evidência ficam no object storage próprio, com SHA-256. |
>
> Specs 001, 002, 005, 011.

---

# 2. Problema

Atualmente sistemas que necessitam de assinatura digital normalmente integram diretamente com fornecedores específicos.

Isso gera:

- acoplamento com APIs proprietárias;
- duplicação de integrações;
- ausência de visão centralizada;
- regras diferentes de assinatura entre sistemas;
- dificuldade de troca de fornecedor;
- ausência de trilha de auditoria corporativa;
- tratamento inconsistente de falhas;
- falta de estratégia unificada de Identity Proofing;
- dependência do fornecedor para recuperação de documentos e evidências;
- dificuldade de reprocessamento;
- pouca observabilidade operacional.

A plataforma deve eliminar esse acoplamento, transformando assinatura digital e validação de identidade em capacidades corporativas reutilizáveis.

---

# 3. Objetivos

A plataforma deverá oferecer uma interface única para qualquer sistema corporativo iniciar, acompanhar e concluir processos de assinatura.

Os principais objetivos são:

1. Centralizar processos de assinatura digital.
2. Desacoplar sistemas consumidores dos fornecedores.
3. Permitir múltiplos provedores.
4. Centralizar Identity Proofing.
5. Preservar todos os artefatos do processo.
6. Garantir rastreabilidade completa.
7. Garantir alta disponibilidade e resiliência.
8. Possibilitar reprocessamento automático e manual.
9. Disponibilizar acompanhamento operacional.
10. Permitir evolução futura das políticas de assinatura sem alteração dos sistemas consumidores.

> **Atendimento dos objetivos**
>
> | # | Objetivo | Estado | Observação |
> |---|---|---|---|
> | 1 | Centralizar processos | Implementado | Seção 6. |
> | 2 | Desacoplar fornecedores | Implementado | Seções 4 e 29. |
> | 3 | Múltiplos provedores | Implementado parcialmente | Três adapters; seleção apenas por configuração, sem roteamento por regra nem fallback (Fase 2). |
> | 4 | Centralizar Identity Proofing | Implementado parcialmente | Seção 9; adapter simulado. |
> | 5 | Preservar artefatos | Implementado | Seções 26 e 27. |
> | 6 | Rastreabilidade | Implementado | Seção 24. |
> | 7 | Disponibilidade e resiliência | Implementado parcialmente | Retry, DLQ, outbox, reconciliação; sem circuit breaker; meta de 99,9% depende de infraestrutura de produção (seção 42). |
> | 8 | Reprocessamento | Implementado | Seções 14 e 15. |
> | 9 | Acompanhamento operacional | Implementado | Seções 34 a 36. |
> | 10 | Evolução de políticas | Planejado | Não há motor de políticas (seção 10). |

---

# 4. Princípios de Arquitetura

A plataforma deverá seguir os seguintes princípios:

**Provider Agnostic**

Nenhum sistema consumidor deve conhecer DocuSign, Clicksign, Serasa, Unico, Datavalid ou qualquer fornecedor específico.

**Capability Based**

O consumidor solicita capacidades.

Exemplo:

```json
{
  "identityValidations": [
    "FACE_MATCH",
    "LIVENESS",
    "DOCUMENT_AUTHENTICITY"
  ]
}
```

e não:

```json
{
  "providers": [
    "UNICO",
    "SERASA"
  ]
}
```

No contrato real o campo se chama `identityProofing.validations` (seções 6 e 9).

---

**Asynchronous First**

Processamentos externos deverão ocorrer predominantemente de forma assíncrona utilizando filas ou tópicos.

---

**API First**

Todas as capacidades deverão estar disponíveis através de APIs documentadas.

---

**Event Driven**

Mudanças relevantes no processo deverão gerar eventos.

---

**Self-Contained**

A plataforma deverá preservar internamente todos os artefatos necessários para comprovação e reconstrução do processo.

---

**Idempotent**

Operações deverão ser projetadas para suportar reexecução sem gerar duplicidades.

---

**Auditable**

Toda ação relevante deverá possuir trilha de auditoria.

---

**Secure by Design**

Segurança, proteção de dados e segregação deverão fazer parte do desenho desde a concepção.

---

> **Estado**
>
> | Princípio | Estado | Observação |
> |---|---|---|
> | Provider Agnostic | Implementado | Validador rejeita campos de fornecedor; contrato do adapter sem tipos de fornecedor (`FakeProviderAdapterTests.Adapter_contract_exposes_no_vendor_types`). |
> | Capability Based | Implementado | 11 capabilities de identity proofing (seção 9). |
> | Asynchronous First | Implementado | Uma fila por domínio sobre RabbitMQ; só a verificação do código de confirmação é síncrona, porque o signatário precisa da resposta (D-076). |
> | API First | Implementado | API versionada em `/v1`, Swagger em `/swagger`, exemplos nos principais endpoints. |
> | Event Driven | Implementado parcialmente | Mudanças geram eventos no journal e callbacks; não há barramento de eventos de domínio para terceiros além dos callbacks. |
> | Self-Contained | Implementado | Seções 26 e 30. |
> | Idempotent | Implementado | Seção 21. |
> | Auditable | Implementado | Seções 24 e 36. |
> | Secure by Design | Implementado parcialmente | OIDC, RBAC, segregação, HMAC, SSRF, mascaramento; WAF, KMS e secrets manager ficam como infraestrutura externa (seção 37). |
>
> Constitution I a VII; specs 001 a 011; D-006, D-010.

---

# 5. Escopo Funcional

A plataforma será composta pelos seguintes domínios funcionais:

| Domínio | Responsabilidade | Estado |
|---|---|---|
| Signature Orchestration | Orquestrar processos de assinatura | Implementado |
| Identity Proofing | Validar identidade dos signatários | Implementado parcialmente (adapter simulado; sessão independente do processo de assinatura) |
| Document Management | Receber e preservar documentos | Implementado (URL ou upload) |
| Provider Integration | Integrar provedores externos | Implementado parcialmente (simulado, DocuSign e Lacuna) |
| Workflow Engine | Controlar o processo | Implementado |
| State Machine | Controlar estados | Implementado (`FAILED` e `EXPIRED` sem gatilho; seção 11) |
| Event Journal | Registrar eventos | Implementado |
| Artifact Management | Gerenciar artefatos | Implementado |
| Callback Management | Notificar consumidores | Implementado |
| Reprocessing | Reexecutar operações | Implementado |
| Reconciliation | Corrigir inconsistências | Implementado |
| Operations Portal | Acompanhamento operacional | Implementado |
| Audit | Garantir rastreabilidade | Implementado |

> Specs 001 a 011. Organização em projetos: [ARCHITECTURE.md](ARCHITECTURE.md).

---

# 6. Criação de Processo de Assinatura

Endpoint principal:

```http
POST /v1/signature-processes
```

O consumidor deverá fornecer um `Idempotency-Key`.

Exemplo:

```http
Idempotency-Key: d5fa67d2-9392-44fe
```

A chave é obrigatória e tem no máximo 128 caracteres. O corpo é validado antes de qualquer consulta de replay, de modo que um corpo inválido responde sempre 400.

## 6.1 Payload

O payload completo, com tudo o que a API aceita:

```json
{
  "externalId": "CONTRACT-928182",

  "document": {
    "fileName": "contrato.pdf",
    "source": {
      "type": "URL",
      "url": "https://documentos.exemplo.com/contrato.pdf"
    }
  },

  "defaults": {
    "signatureType": "ADVANCED",
    "confirmation": ["EMAIL", "SMS"]
  },

  "signers": [
    {
      "externalId": "customer-123",
      "name": "Maria Exemplo",
      "document": "<cpf-11-digitos>",
      "email": "maria@example.com",
      "phone": "+5511999990000",
      "order": 1
    },
    {
      "name": "João Exemplo",
      "document": "<cpf-11-digitos>",
      "email": "joao@example.com",
      "order": 2,
      "signatureType": "QUALIFIED",
      "confirmation": ["EMAIL", "WHATSAPP"],
      "phone": "+5511988880000"
    }
  ],

  "identityProofing": {
    "validations": [
      { "type": "FACE_MATCH", "required": true },
      "LIVENESS"
    ]
  },

  "callback": {
    "url": "https://cliente.exemplo.com/signatures/callback"
  }
}
```

O payload mínimo e o formato da versão 1.0 continuam válidos: só `externalId`, `document`, `signers[].name`, `signers[].document` e um tipo de assinatura (por signatário, em `defaults.signatureType` ou no legado `signature.type`).

| Campo | Obrigatório | Regras |
|---|---|---|
| `externalId` | sim | Até 200 caracteres. |
| `document.fileName` | sim | Truncado em 300 caracteres ao armazenar. |
| `document.source.type` | sim | `URL` (http/https absoluta) ou `UPLOAD`. |
| `document.source.url` | se `URL` | O documento é buscado pela plataforma (limite de 50 MiB e 30 s). |
| `document.source.uploadId` | se `UPLOAD` | Obtido em `POST /v1/document-uploads`; vale 24 h e é do cliente que enviou. |
| `defaults.signatureType` | não | `SIMPLE`, `ADVANCED` ou `QUALIFIED`. |
| `defaults.confirmation` | não | Lista de canais `EMAIL`, `SMS`, `WHATSAPP`, sem repetição. |
| `signature.type` | não | Formato legado; usado como último recurso para o tipo. |
| `signers` | sim | Ao menos um. Não há limite máximo no código. |
| `identityProofing.validations` | não | Capabilities do catálogo (seção 9). Ver estado abaixo. |
| `callback.url` ou `callback.callbackId` | não | Um ou outro, nunca os dois (seção 8). |

### Signatários

| Campo | Obrigatório | Regras |
|---|---|---|
| `name` | sim | Até 300 caracteres. |
| `document` | sim | CPF válido, 11 dígitos, só números (dígito verificador conferido). |
| `externalId` | não | Padrão `signer-<n>`; até 200 caracteres. |
| `email` | condicional | Obrigatório se o canal `EMAIL` for usado; até 254 caracteres. |
| `phone` | condicional | Obrigatório se `SMS` ou `WHATSAPP` for usado; 10 a 15 dígitos, `+` opcional; normalizado. |
| `signatureType` | condicional | Precedência: signatário, depois `defaults.signatureType`, depois `signature.type`. Se nenhum, erro no signatário. |
| `confirmation` | não | Substitui `defaults.confirmation`; `[]` desliga a herança. |
| `order` | não | Inteiro maior ou igual a 1. |

**Ordem de assinatura.** Sem `order` em ninguém, todos assinam em paralelo. Se um signatário informa `order`, todos devem informar. Valores iguais formam um grupo paralelo; os valores distintos devem ser `1..k` sem lacunas, e o erro aponta o primeiro signatário além da lacuna. Cada grupo só assina depois que o anterior terminou.

**Tipos de assinatura.** `SIMPLE`, `ADVANCED` e `QUALIFIED`. O tipo do processo é o legado `signature.type`, se válido, ou o mais alto entre os signatários. A plataforma registra o tipo pedido; a configuração específica de assinatura avançada ou qualificada em cada fornecedor não é feita (seção 54).

**Canais de confirmação.** `EMAIL`, `SMS` e `WHATSAPP`. O contato do canal é validado na criação. O envio do código é feito por um notificador atrás da interface `IConfirmationNotifier`; hoje só existe o notificador simulado (seção 6.3).

## 6.2 Resposta, replay e erros

Primeira criação: **202 Accepted**, com `Location: /v1/signature-processes/{processId}`:

```json
{
  "processId": "sig_0199a1b2c3d4",
  "externalId": "CONTRACT-928182",
  "businessStatus": "CREATED",
  "operationalStatus": "READY",
  "createdAt": "2026-10-06T12:00:00Z"
}
```

Mesma chave e mesmo corpo (dentro de 24 h): **200 OK** com o mesmo processo. O hash do corpo é calculado sobre o JSON canônico, então ordem de propriedades e espaços não importam. Mesma chave e corpo diferente: **409** `Idempotency conflict`. O escopo da chave é do chamador: cliente de API (`c:`), pessoa autenticada (`u:`) ou, sem autenticação, a chave crua. Duas pessoas com a mesma chave não reaproveitam o processo uma da outra (D-090).

| HTTP | Quando |
|---|---|
| 400 `Validation failed` | Corpo ou campo inválido, `Idempotency-Key` ausente ou maior que 128, campo de fornecedor. O corpo traz `errors` com a chave do campo, por exemplo `signers[1].phone`. |
| 401 / 403 | Sem token válido ou papel não permitido (seção 37). |
| 409 `Idempotency conflict` | Chave reutilizada com outro corpo. |
| 429 | Limite de criação por cliente (600 por minuto por padrão), sem corpo e sem `Retry-After`. |

Exemplo de erro de validação:

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Validation failed",
  "status": 400,
  "detail": "Validation failed",
  "correlationId": "cor_0199a1b2c3d4e5f6a7b8c9d0",
  "errors": {
    "signers[1].phone": ["Required when the confirmation channel SMS or WHATSAPP is used"]
  }
}
```

O catálogo completo de erros está na seção 43.

## 6.3 Confirmação por código

Quando o processo tem canais de confirmação, nenhum signatário assina antes de confirmar todos os seus canais, e, havendo `order`, só na vez do seu grupo.

| Parâmetro | Valor padrão | Configuração |
|---|---|---|
| Tamanho do código | 6 dígitos | `Confirmation:CodeLength` |
| Validade | 600 s | `Confirmation:CodeTtlSeconds` |
| Tentativas erradas até bloquear | 5 | `Confirmation:MaxAttempts` |
| Intervalo mínimo de reenvio | 30 s | `Confirmation:ResendMinIntervalSeconds` |
| Envios por canal (incluindo o primeiro) | 5 | `Confirmation:MaxSends` |

Estados da confirmação por canal: `PLANNED`, `SENDING`, `SENT`, `CONFIRMED` e `LOCKED`. O estado `EXPIRED` é derivado na resposta (código enviado e já vencido); não é gravado.

- Só o **HMAC-SHA256 do código** é guardado, com chave própria; a comparação é em tempo constante e o hash é apagado ao confirmar ou bloquear. O código nunca aparece em log, evento do journal, operação ou mensagem do broker.
- O código errado responde **422** com `attemptsRemaining`; ao esgotar, **423** `Code locked`; código vencido responde **410** `Code expired` e não consome tentativa nem bloqueia.
- O reenvio responde **202**; acima do teto de envios ou antes do intervalo, **429** (com `Retry-After` no caso do intervalo). Reenviar a um canal bloqueado emite código novo e zera as tentativas.
- O envio (`CONFIRMATION_SEND`) é uma operação assíncrona com retry e DLQ próprios (domínio `notification`) e **auxiliar**: não altera o estado operacional do processo. A verificação (`CONFIRMATION_VERIFY`) é gravada na própria requisição como operação já concluída.
- Cada tentativa gera evento: `CONFIRMATION_CONFIRMED`, `CONFIRMATION_CODE_REJECTED`, `CONFIRMATION_LOCKED`, `CONFIRMATION_CODE_EXPIRED`.
- O notificador simulado grava o código em uma tabela de desenvolvimento; a leitura em `GET /v1/dev/confirmation-codes/{processId}` só existe com `Confirmation:ExposeSink=true` e papel `admin`, e é ligada apenas no compose.

**Liberação no provider.** Com o provider simulado, cada signatário é liberado individualmente quando confirma e é a sua vez. DocuSign e Lacuna não têm liberação por signatário: os códigos vão a todos e o documento só é enviado ao provider quando **todos** os canais de **todos** os signatários estão confirmados; a ordem passa a ser delegada ao provider (D-085).

## 6.4 Upload de documento

```http
POST /v1/document-uploads
Content-Type: multipart/form-data   (campo "file")
```

Papéis `client`, `operator` e `admin`. Responde **201** com `uploadId`, `fileName`, `contentType`, `size`, `sha256` e `expiresAt` (24 h). O tamanho máximo configurado é 50 MiB. Cliente só usa os próprios uploads; upload feito por pessoa só serve a pessoas; pessoas usam qualquer upload. Uploads não consumidos não são removidos (seção 54).

## 6.5 Identificadores e correlação

Prefixos dos ids: `sig_` (processo), `sgn_` (signatário), `op_` (operação), `evt_` (evento), `art_` (artefato), `upl_` (upload), `prf_` (sessão de proofing), `cbd_` (entrega de callback). O header `X-Correlation-Id` é aceito (até 128 caracteres), gerado quando ausente e devolvido sempre; aparece em toda resposta de erro e no journal.

> **Estado:** Implementado parcialmente.
>
> - **Implementado:** criação idempotente, signatários com tipo, ordem e canais, `defaults`, confirmação por código, upload, callback por URL ou id, validação por campo, 202/200/409.
> - **Falta:** (a) `output.destination` da versão 1.0 **não existe**: o campo é aceito e ignorado, e a entrega ao destino do consumidor (operação `OUTPUT_DELIVERY`) nunca foi construída (seção 30); (b) `identityProofing.validations` no processo é **validado e registrado** (devolvido no `GET` como `identityValidations` e no journal como `IDENTITY_VALIDATION_REQUESTED`), mas **não dispara validação nem bloqueia a assinatura**; a validação real roda em sessão própria (seção 9); (c) não há limite de quantidade de signatários nem de tamanho do corpo.
>
> Specs 001, 002, 010, 011. D-003, D-004, D-072 a D-075, D-079, D-080, D-085, D-090.

---

# 7. Callback

A URL informada pelo consumidor deverá ser utilizada como destino de eventos do processo.

O callback será assíncrono.

Exemplo:

```http
POST https://cliente.exemplo.com/signatures/callback
```

Payload:

```json
{
  "eventId": "evt_891726",

  "eventType": "SIGNATURE_PROCESS.COMPLETED",

  "processId": "sig_872364",

  "externalId": "CONTRACT-928182",

  "status": "COMPLETED",

  "occurredAt": "2026-10-06T02:41:22Z",

  "document": {
    "id": "art_18273",
    "downloadUrl": "https://api.exemplo.com/v1/downloads/art_18273?expires=1790000000&sig=..."
  }
}
```

O campo `document` só vem quando o status é `COMPLETED` e existe o documento assinado; `downloadUrl` é um link assinado de curta duração (seção 31). Valores nulos são omitidos. O corpo não leva dados pessoais.

Callbacks deverão possuir autenticação e garantia de integridade.

Headers enviados:

```http
X-Signature-Event-Id: evt_891726
X-Signature-Timestamp: 1790000000
X-Signature-Signature: sha256=<hex>
```

A assinatura é `sha256=` seguido do hexadecimal de `HMAC-SHA256(segredo, "{timestamp}.{corpo}")`. O receptor deve conferir a assinatura em tempo constante e rejeitar timestamps fora da tolerância (300 s por padrão). O `eventId` é estável entre as tentativas, para o receptor deduplicar.

Falhas de callback deverão possuir retry independente do processo principal.

**Eventos emitidos.** O `eventType` é `SIGNATURE_PROCESS.<STATUS>` para os estados `SIGNATURE_IN_PROGRESS`, `PARTIALLY_SIGNED`, `SIGNED`, `COMPLETED`, `REJECTED` e `CANCELLED`. `FAILED` e `EXPIRED` também são notificáveis, mas nenhum fluxo leva o processo a esses estados hoje (seção 11).

**Entrega.** Cada emissão é uma operação `CALLBACK_SEND` (domínio `callback`), gravada na mesma transação da mudança de estado. Responder 2xx conta como entregue; 408, 429, 5xx e falha de rede são transitórios (retry com backoff, depois `callback-dlq`); os demais 4xx e qualquer 3xx são permanentes. Não se seguem redirecionamentos. O timeout é de 10 s. A operação é auxiliar: nunca altera o estado do processo e roda mesmo com o processo terminal.

**Consulta e reenvio.** `GET /v1/signature-processes/{id}/callbacks` lista as entregas (`status`: `PENDING`, `RETRY_PENDING`, `DELIVERED`, `FAILED`, `DLQ`; tentativas; último código HTTP e erro). O reenvio manual usa `POST /v1/signature-processes/{id}/retry` com o `operationId` da entrega, inclusive com o processo já terminal.

> **Estado:** Implementado.
>
> Não há garantia de ordem entre callbacks (consumidores devem validar o estado atual). Specs 004, 001. D-033, D-034, D-036, D-040.

---

# 8. Segurança de Callback

URLs arbitrárias representam risco de SSRF.

A solução deverá suportar preferencialmente callbacks previamente registrados.

Exemplo:

```json
{
  "callbackId": "ORIGINACAO_SIGNATURE_CALLBACK"
}
```

O cadastro deverá mapear:

```text
ORIGINACAO_SIGNATURE_CALLBACK
        ↓
https://originacao.empresa.com/signature/callback
```

Quando URLs dinâmicas forem permitidas deverão existir:

- HTTPS obrigatório;
- bloqueio de localhost;
- bloqueio de redes privadas;
- proteção contra DNS rebinding;
- controle de egress;
- allowlist opcional;
- limite de redirecionamentos;
- timeout;
- rate limit.

## 8.1 Callbacks registrados

| Método e rota | Papéis | Resposta |
|---|---|---|
| `POST /v1/callbacks` | `client`, `admin` | 201 com `Location`; **o segredo só aparece nesta resposta**. 409 se o `callbackId` já existe. |
| `GET /v1/callbacks` | todos | Lista (cliente vê só os seus). |
| `GET /v1/callbacks/{callbackId}` | todos | Detalhe, sem segredo. |
| `DELETE /v1/callbacks/{callbackId}` | `client`, `admin` | 204; apenas desativa (o id não pode ser reutilizado). |

Corpo do cadastro:

```json
{
  "callbackId": "ORIGINACAO_SIGNATURE_CALLBACK",
  "url": "https://originacao.exemplo.com/signature/callback",
  "secret": "<opcional, 16 a 200 caracteres>",
  "description": "opcional"
}
```

O `callbackId` segue `^[A-Za-z0-9_.-]{1,100}$`. Sem `secret`, a plataforma gera um de 32 bytes. Callbacks cadastrados por `admin` (sem dono) servem a qualquer processo, mas ficam invisíveis na listagem de clientes; um `callbackId` de outro cliente falha como "desconhecido", sem revelar existência (D-068). URLs dinâmicas assinam com o segredo padrão configurado (`Callbacks:DefaultSecret`).

## 8.2 Controles contra SSRF

| Controle do PRD | Estado | Como é feito |
|---|---|---|
| HTTPS obrigatório | Implementado | Exigido por padrão; `Callbacks:AllowHttp` o relaxa (só desenvolvimento). Credenciais na URL são recusadas. |
| Bloqueio de localhost e redes privadas | Implementado | Bloqueia loopback, redes privadas, link-local, CGNAT e endereços de metadata; `Callbacks:AllowPrivateNetworks` o relaxa (só desenvolvimento). |
| Proteção contra DNS rebinding | Implementado | O DNS é resolvido uma vez e a conexão vai ao IP validado; cada IP é conferido no momento da conexão. |
| Allowlist opcional | Implementado | `Callbacks:AllowedHosts`, com curinga. |
| Limite de redirecionamentos | Implementado | Nenhum: redirecionamentos não são seguidos e 3xx é falha permanente. |
| Timeout | Implementado | 10 s por entrega. |
| Rate limit | Implementado parcialmente | 10 requisições por segundo por host de destino, em memória por instância do worker (não distribuído). |
| Controle de egress | Planejado | A proteção está na aplicação; firewall e controle de saída de rede são infraestrutura externa. |

A mesma guarda pode ser aplicada à busca do documento de origem (`Artifacts:BlockPrivateNetworks`), **desligada por padrão** (seção 54).

> **Estado:** Implementado parcialmente (egress e rate limit distribuído pendentes).
>
> Specs 004, 009. D-034, D-035, D-038, D-039, D-067, D-068.

---

# 9. Identity Proofing

A plataforma deverá permitir que o sistema consumidor determine quais evidências de identidade são necessárias.

Capacidades previstas:

| Capability | Descrição | Evidência exigida |
|---|---|---|
| PERSON_DATA | Validação cadastral | nenhuma |
| DOCUMENT_DATA | Extração dos dados do documento | `DOCUMENT_FRONT` |
| DOCUMENT_AUTHENTICITY | Documentoscopia | `DOCUMENT_FRONT` |
| DOCUMENT_OWNERSHIP | Documento pertence à pessoa | `DOCUMENT_FRONT` |
| FACE_MATCH | Comparação facial | `SELFIE` e `DOCUMENT_FRONT` |
| LIVENESS | Prova de vida | `SELFIE` |
| GOVERNMENT_BIOMETRIC_MATCH | Comparação com base oficial | `SELFIE` |
| PHONE_OWNERSHIP | Validação do telefone | `subject.phone` |
| EMAIL_OWNERSHIP | Validação do e-mail | `subject.email` |
| DEVICE_RISK | Avaliação de dispositivo | `subject.deviceId` |
| IDENTITY_RISK | Score agregado de identidade | roda por último, depois das demais |

Exemplo:

```json
{
  "identityProofing": {
    "validations": [
      {
        "type": "FACE_MATCH",
        "required": true
      },
      {
        "type": "LIVENESS",
        "required": true
      },
      {
        "type": "GOVERNMENT_BIOMETRIC_MATCH",
        "required": false
      }
    ]
  }
}
```

## 9.1 Sessão de proofing

Identity Proofing é um contexto separado da assinatura: uma **sessão** (`prf_...`) pode existir sem nenhum processo de assinatura.

```http
POST /v1/proofing-sessions            (Idempotency-Key obrigatório; client, admin)
```

```json
{
  "externalId": "KYC-001",
  "subject": {
    "name": "Maria Exemplo",
    "document": "<cpf-11-digitos>",
    "phone": "5511999990000",
    "email": "maria@example.com",
    "deviceId": "dev-123"
  },
  "validations": [
    { "type": "FACE_MATCH", "required": true },
    "LIVENESS",
    { "type": "PHONE_OWNERSHIP", "required": false }
  ]
}
```

Resposta **202** (ou **200** no replay): `{ "sessionId": "prf_...", "externalId": "KYC-001", "status": "WAITING_EVIDENCE", "result": "PENDING", "createdAt": "..." }`. Capability desconhecida ou repetida e campos de fornecedor são rejeitados por campo; o `phone` da sessão aceita só dígitos (10 a 15).

| Método e rota | Papéis | Finalidade |
|---|---|---|
| `GET /v1/proofing-sessions/{id}` | todos | Sessão, validações e evidências (metadados e hash). |
| `POST /v1/proofing-sessions/{id}/documents` | `client`, `admin` | Evidência `DOCUMENT_FRONT` ou `DOCUMENT_BACK`. |
| `POST /v1/proofing-sessions/{id}/biometrics` | `client`, `admin` | Evidência `SELFIE`. |
| `GET /v1/proofing-sessions/{id}/result` | todos | Resultado agregado e validações com `score` e detalhes. |
| `GET /v1/proofing-sessions/{id}/operations` | todos | Operações da sessão (D-047). |
| `GET /v1/proofing-sessions/{id}/events` | todos | Eventos da sessão. |
| `POST /v1/proofing-sessions/{id}/retry` | `operator`, `client`, `admin` | Reprocessa uma validação com erro. |
| `DELETE /v1/proofing-sessions/{id}/evidence` | `operator`, `client`, `admin` | Apaga evidências (sessão concluída). |

Evidência: JSON com `type`, `contentType` (`image/jpeg` ou `image/png`) e `content` em base64, até 5 MiB; uma por tipo (repetir responde 409).

**Estados.** Sessão: `WAITING_EVIDENCE`, `IN_PROGRESS`, `COMPLETED`. Resultado: `PENDING`, `APPROVED`, `REJECTED` (`REJECTED` se alguma validação obrigatória falhou). Validação: `WAITING_EVIDENCE`, `PENDING`, `PASSED`, `FAILED`, `ERROR`. Uma validação em `ERROR` (falha permanente ou DLQ) mantém a sessão aberta até o reprocessamento.

**Execução.** Cada validação é uma operação `IDENTITY_VALIDATION` na fila `identity-proofing`, com retry e DLQ, executada assim que a evidência chega. `IDENTITY_RISK` espera todas as outras validações terminarem e é calculado como `1 − média dos scores` (falha vale 0); passa se o risco for menor que 0,5. As atualizações da sessão são serializadas por bloqueio de linha.

**Dados sensíveis.** Evidências **nunca são baixáveis**; só metadados e hash. Retenção padrão de 30 dias após a conclusão, com exclusão manual; ao excluir, os hashes permanecem no journal (`EVIDENCE_DELETED`). CPF, telefone e e-mail saem mascarados.

> **Estado:** Implementado parcialmente.
>
> - **Implementado:** as 11 capabilities, sessões, evidências, resultado agregado, retry, DLQ, retenção e exclusão, segregação por cliente.
> - **Falta:** (a) existe **um único adapter, simulado**: não há integração com fornecedor real de identidade, então o desacoplamento está provado só pela ausência de tipos de fornecedor no contrato; (b) a sessão **não está ligada ao processo de assinatura** (nenhuma assinatura espera uma sessão aprovada); (c) não há callback ao fim da sessão (consulta por polling) nem expiração de sessão; (d) o portal mostra só as capabilities pedidas, não o resultado da sessão.
>
> Specs 005, 009. D-041 a D-048, D-067. Política de dados: [DATA-PROTECTION.md](DATA-PROTECTION.md).

---

# 10. Políticas de Identity Proofing

Além da seleção explícita de capabilities, a plataforma deverá evoluir para políticas corporativas.

Exemplo:

```json
{
  "identityProofing": {
    "policy": "HIGH_RISK_ONBOARDING"
  }
}
```

A política poderá representar:

```text
HIGH_RISK_ONBOARDING

PERSON_DATA
DOCUMENT_AUTHENTICITY
FACE_MATCH
LIVENESS
GOVERNMENT_BIOMETRIC_MATCH
DEVICE_RISK
```

Isso permitirá que políticas sejam alteradas sem mudanças nos sistemas consumidores.

> **Estado:** Planejado.
>
> Não existe entidade, tabela nem motor de políticas; o campo `policy` não é aceito. O consumidor seleciona capabilities explicitamente. As regras de `IDENTITY_RISK` são código fixo. Previsto para a Fase 2 (seção 47) e Fase 3 (seção 48). Spec 005 (fora de escopo, seção 10 citada).

---

# 11. Máquina de Estados

Deverão existir máquinas de estado independentes.

## 11.1 Estado de Negócio

```text
CREATED

DOCUMENT_RECEIVED

VALIDATING

READY_FOR_SIGNATURE

SIGNATURE_IN_PROGRESS

PARTIALLY_SIGNED

SIGNED

FINALIZING

COMPLETED
```

Estados terminais:

```text
FAILED

CANCELLED

EXPIRED

REJECTED
```

Na implementação, `COMPLETED` também é terminal (nenhuma transição sai dele), o que dá cinco estados terminais: `COMPLETED`, `FAILED`, `CANCELLED`, `EXPIRED` e `REJECTED`.

**Transições permitidas.** A partir de qualquer estado não terminal é possível ir a `FAILED`, `CANCELLED` ou `EXPIRED`. Fora isso, só estas:

| Origem | Destinos |
|---|---|
| `CREATED` | `DOCUMENT_RECEIVED` |
| `DOCUMENT_RECEIVED` | `VALIDATING` |
| `VALIDATING` | `READY_FOR_SIGNATURE` |
| `READY_FOR_SIGNATURE` | `SIGNATURE_IN_PROGRESS` |
| `SIGNATURE_IN_PROGRESS` | `PARTIALLY_SIGNED`, `SIGNED`, `REJECTED` |
| `PARTIALLY_SIGNED` | `SIGNED`, `REJECTED` |
| `SIGNED` | `FINALIZING` |
| `FINALIZING` | `COMPLETED` |
| terminais | nenhum |

`REJECTED` só é alcançável a partir de `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED`. Uma transição inválida é erro permanente (`409 Invalid state transition` quando pedida pela API). Os estados são devolvidos como texto em `businessStatus`.

> **Estado:** Implementado parcialmente.
>
> - **Implementado:** todos os estados e transições acima, validados em um único ponto, com testes de cada transição válida, inválida e terminal.
> - **Falta:** `FAILED` e `EXPIRED` **existem na máquina, mas nenhum fluxo leva o processo até eles**. Uma falha permanente de operação mantém o estado de negócio atual e coloca o estado operacional em `MANUAL_ACTION` (seção 16); não há expiração de processo nem limite de tempo de assinatura (o polling do provider não tem fim). Por isso callbacks `FAILED` e `EXPIRED` nunca são emitidos e a métrica de falhas só conta `REJECTED` na prática.
>
> Specs 001, 006. D-007, D-012, D-014.

---

# 12. Estado Operacional

O estado operacional deverá ser independente do estado de negócio.

```text
READY

PROCESSING

RETRY_PENDING

SUSPENDED

DLQ

MANUAL_ACTION
```

Exemplo:

```text
Business Status:
SIGNED

Operational Status:
RETRY_PENDING
```

Isso significa que a assinatura ocorreu corretamente, mas alguma operação posterior ainda precisa ser concluída.

**Transições permitidas.**

| Origem | Destinos |
|---|---|
| `READY` | `PROCESSING`, `SUSPENDED`, `MANUAL_ACTION` |
| `PROCESSING` | `READY`, `RETRY_PENDING`, `SUSPENDED`, `DLQ`, `MANUAL_ACTION` |
| `RETRY_PENDING` | `PROCESSING`, `READY`, `SUSPENDED`, `DLQ`, `MANUAL_ACTION` |
| `SUSPENDED` | `READY` |
| `MANUAL_ACTION` | `READY` |
| `DLQ` | `READY`, `MANUAL_ACTION` |

O estado operacional de um processo só muda por operações **principais**; as operações auxiliares (`CALLBACK_SEND`, `CONFIRMATION_SEND`) nunca o alteram, para que uma DLQ de notificação ou callback não bloqueie o polling de assinatura (D-033, D-076). Cancelar um processo devolve o estado operacional a `READY`.

> **Estado:** Implementado parcialmente.
>
> `SUSPENDED` existe na máquina, mas **nenhum fluxo ou endpoint leva o processo a ele** (não há ação de suspender). Os demais estados são exercitados em testes (`StateMachineTests.Operational_transitions`, `RetryTests.Retry_pending_state_exposes_attempt_max_attempts_and_next_retry_time_and_cancel_stops_it`).
>
> Specs 001, 003. D-011, D-012, D-033, D-076.

---

# 13. Operações

Toda atividade executada pelo orquestrador deverá ser registrada como uma operação independente.

Exemplos:

```text
DOCUMENT_DOWNLOAD

DOCUMENT_STORE

IDENTITY_VALIDATION

PROVIDER_CREATE_PROCESS

PROVIDER_SEND_DOCUMENT

PROVIDER_STATUS_CHECK

SIGNED_DOCUMENT_DOWNLOAD

SIGNED_DOCUMENT_STORE

CALLBACK_SEND

OUTPUT_DELIVERY
```

Além dessas, a implementação acrescentou `CONFIRMATION_SEND` e `CONFIRMATION_VERIFY` (spec 010). Os doze tipos reais, com a fila de cada um:

| Tipo | Domínio / fila | Observação |
|---|---|---|
| `DOCUMENT_DOWNLOAD` | `artifact` | Busca o documento (URL ou upload) e guarda o original. |
| `DOCUMENT_STORE` | `artifact` | Confere o original armazenado. |
| `IDENTITY_VALIDATION` | `identity-proofing` | Uma validação de uma sessão de proofing. |
| `PROVIDER_CREATE_PROCESS` | `signature-provider` | Cria o processo no provider (idempotente por referência externa). |
| `PROVIDER_SEND_DOCUMENT` | `signature-provider` | Envia o documento; adiado até todas as confirmações nos providers sem liberação por signatário. |
| `PROVIDER_STATUS_CHECK` | `signature-provider` | Polling do status; cada consulta é uma nova operação, reagendada a cada 2 s. |
| `SIGNED_DOCUMENT_DOWNLOAD` | `artifact` | Baixa o assinado e a evidência. |
| `SIGNED_DOCUMENT_STORE` | `artifact` | Confere os artefatos finais e conclui o processo. |
| `CALLBACK_SEND` | `callback` | Auxiliar. |
| `CONFIRMATION_SEND` | `notification` | Auxiliar. |
| `CONFIRMATION_VERIFY` | `notification` | Registro de auditoria síncrono, já concluído. |
| `OUTPUT_DELIVERY` | `callback` | **Declarado, nunca criado nem executado** (seção 30). |

**Fluxo normal.** `DOCUMENT_DOWNLOAD` → `DOCUMENT_STORE` → `PROVIDER_CREATE_PROCESS` → `PROVIDER_SEND_DOCUMENT` → `PROVIDER_STATUS_CHECK` (repetido) → `SIGNED_DOCUMENT_DOWNLOAD` → `SIGNED_DOCUMENT_STORE`. Cada execução é uma única transação que grava operação, estados, journal, a próxima operação e a mensagem de outbox.

Modelo:

```json
{
  "operationId": "op_938273",

  "processId": "sig_12345",

  "type": "SIGNED_DOCUMENT_DOWNLOAD",

  "status": "RETRY_PENDING",

  "attempt": 3,

  "maxAttempts": 8,

  "nextRetryAt": "2026-10-06T03:12:00Z",

  "output": null,

  "error": { "class": "TRANSIENT", "message": "..." },

  "createdAt": "2026-10-06T03:00:00Z",

  "updatedAt": "2026-10-06T03:05:00Z"
}
```

`status` da operação: `NOT_STARTED`, `PROCESSING`, `RETRY_PENDING`, `DLQ`, `COMPLETED`, `FAILED`, `CANCELLED`. O valor `PROCESSING` existe no enum, mas a execução é curta e atômica, então ele não é persistido. Consulta paginada em `GET /v1/signature-processes/{id}/operations`.

> **Estado:** Implementado parcialmente.
>
> `OUTPUT_DELIVERY` não tem handler (seção 30). Specs 001, 002, 003, 010. D-011, D-013, D-026, D-044, D-076.

---

# 14. Reprocessamento

A solução deverá possuir mecanismos nativos de reprocessamento.

Uma falha não deverá obrigatoriamente reiniciar todo o processo.

Exemplo:

```text
DOCUMENT_STORE              COMPLETED

IDENTITY_PROOFING           COMPLETED

PROVIDER_CREATE_PROCESS     COMPLETED

SIGNATURE                   COMPLETED

SIGNED_DOCUMENT_DOWNLOAD    FAILED

SIGNED_DOCUMENT_STORE       NOT_STARTED

CALLBACK                    NOT_STARTED
```

O reprocessamento deverá começar em:

```text
SIGNED_DOCUMENT_DOWNLOAD
```

## Contrato

```http
POST /v1/signature-processes/{id}/retry
```

Corpo opcional:

```json
{
  "operationId": "op_938273",
  "reason": "provedor voltou a responder"
}
```

`reason` tem no máximo 500 caracteres.

Resposta **200**:

```json
{
  "processId": "sig_0199a1b2c3d4",
  "operationId": "op_938273",
  "operationType": "SIGNED_DOCUMENT_DOWNLOAD",
  "operationalStatus": "READY"
}
```

**Regras**

- Só operações em `FAILED`, `DLQ` ou `RETRY_PENDING` são reprocessáveis.
- Sem `operationId`, a plataforma escolhe a operação reprocessável mais recente, ignorando `CALLBACK_SEND`; processo terminal responde 409.
- Com `operationId`, a operação deve pertencer ao processo (404) e estar em um dos três estados (409). Processo terminal só aceita reprocessar `CALLBACK_SEND`.
- O reprocessamento **recomeça na operação que falhou**: operações já concluídas nunca são repetidas. A operação volta a `NOT_STARTED` com tentativas zeradas (orçamento novo); o erro anterior permanece no journal.
- As dead letters da operação são marcadas como resolvidas, o estado operacional volta a `READY` e a ação gera `OPERATION_REPROCESS_REQUESTED`, com ator e motivo.
- Reprocessamentos concorrentes da mesma operação são aceitos uma única vez.

O reprocessamento de uma validação de identidade usa `POST /v1/proofing-sessions/{id}/retry` (seção 9). O reprocessamento é permitido a `operator`, `client` (nos próprios processos) e `admin`.

> **Estado:** Implementado.
>
> Reprocessar **a partir de uma operação já concluída**, invalidando as seguintes, é o "reprocessamento avançado" da Fase 2 e não existe (seção 47). Specs 003, 004, 005. D-025, D-036, D-040, D-047.

---

# 15. Retry

Falhas transitórias deverão gerar retry automático.

Estratégia padrão:

```text
Tentativa 1   imediata

Tentativa 2   +5 segundos

Tentativa 3   +30 segundos

Tentativa 4   +2 minutos

Tentativa 5   +10 minutos

Tentativa 6   +30 minutos

Tentativa 7   +2 horas

Tentativa 8   +6 horas
```

A política deverá ser configurável por tipo de operação.

Deverá ser utilizado:

**Exponential Backoff + Jitter.**

## Como foi implementado

- Cronograma padrão exatamente o acima, em segundos: `0, 5, 30, 120, 600, 1800, 7200, 21600`, com **8 tentativas** no máximo para todos os tipos de operação.
- **Jitter** uniforme de ±20%: o atraso é multiplicado por um fator em `[0,8; 1,2]` (`Retry:Jitter`).
- Configuração por tipo em `Retry:Operations:<TIPO_DA_OPERAÇÃO>:*` (`MaxAttempts`, `DelaysSeconds`, `BaseDelaySeconds`, `Multiplier`, `MaxDelaySeconds`); `Retry:Default:*` vale para os demais. Sem lista de atrasos, usa-se backoff exponencial `base × multiplicador^(n−1)` limitado por `MaxDelaySeconds`.
- O reagendamento é gravado como mensagem de outbox com `available_at` no horário do retry; a operação mostra `attempt`, `maxAttempts` e `nextRetryAt` (seção 13). Não há fila de retry com TTL.
- Erros `UNKNOWN` têm no máximo 3 tentativas (`Retry:UnknownMaxAttempts`).
- Falhas de infraestrutura (banco indisponível) **não consomem tentativa**: a mensagem volta à fila (D-028, D-031).
- Cancelar o processo cancela as operações em `RETRY_PENDING`.
- O `docker-compose.yml` usa um cronograma curto (4 tentativas, 1 s) apenas para demonstração local; o padrão da aplicação é o do PRD (D-030).

> **Estado:** Implementado.
>
> Specs 003, 001. D-024, D-028, D-030, D-031, D-032.

---

# 16. Classificação de Erros

Os erros deverão ser classificados.

### TRANSIENT

Exemplos:

```text
Timeout

HTTP 408

HTTP 429

HTTP 500

HTTP 502

HTTP 503

HTTP 504

Network Failure
```

Resultado:

```text
RETRY_PENDING
```

### PERMANENT

Exemplos:

```text
CPF inválido

Documento inválido

Payload inválido

Assinante inexistente

Operação não permitida
```

Resultado:

```text
FAILED
```

ou:

```text
MANUAL_ACTION
```

### UNKNOWN

Deverá executar quantidade limitada de retries e posteriormente enviar para DLQ.

## Como foi implementado

| Classe | O que cai nela | Resultado |
|---|---|---|
| `TRANSIENT` | Falha marcada como transitória pelo componente (store, provider, callback, notificador); HTTP 408, 429 e qualquer 5xx; timeout, erro de rede e cancelamento por tempo. | Operação em `RETRY_PENDING`, estado operacional `RETRY_PENDING`; esgotadas as tentativas, DLQ. |
| `PERMANENT` | Outros 4xx de provider, origem ou destino; transição de estado inválida; erro de validação; documento acima do limite; destino de callback bloqueado por SSRF; credencial de provider ausente (nomeando a variável). | **Operação `FAILED` e estado operacional `MANUAL_ACTION`**, sem retry. O estado de negócio do processo **não** muda. |
| `UNKNOWN` | Qualquer outra exceção. | Até 3 tentativas e depois DLQ. |

O resultado do PRD "FAILED ou MANUAL_ACTION" materializou-se assim: a operação fica `FAILED` e o processo, `MANUAL_ACTION`, aguardando reprocessamento ou cancelamento. Erros HTTP dos providers reais: 401, 408, 429, 5xx e rede são transitórios; os demais 4xx, permanentes; o corpo de erro é truncado em 300 caracteres (D-087). Todos os eventos de falha vão ao journal (`OPERATION_RETRY_SCHEDULED`, `OPERATION_FAILED`, `OPERATION_DEAD_LETTERED`).

> **Estado:** Implementado.
>
> Specs 003, 011. D-012, D-022, D-028, D-087.

---

# 17. Dead Letter Queue

Cada domínio deverá possuir mecanismo de DLQ.

Exemplo:

```text
signature-provider-dlq

identity-proofing-dlq

artifact-dlq

callback-dlq
```

Mensagens não deverão carregar documentos ou dados sensíveis completos.

Devem carregar apenas referências.

## Como foi implementado

Além dos quatro domínios do exemplo, a spec 010 acrescentou `notification-dlq` (envio de códigos de confirmação). Cada domínio tem uma fila de comando com o nome do domínio e uma DLQ `<domínio>-dlq` (detalhes em [ARCHITECTURE.md](ARCHITECTURE.md)).

- **Esgotadas as tentativas**: a operação vai a `DLQ`, o processo ao estado operacional `DLQ`, é gravada uma entrada na tabela `dead_letter_entry` (consulta e resolução) e uma mensagem de referências é publicada, via outbox, na fila `<domínio>-dlq`.
- **Mensagem malformada** (JSON inválido ou sem identificadores) é rejeitada ao broker e vai à DLQ do domínio, sem derrubar o consumidor.
- A mensagem de DLQ carrega só `processId`, `operationId`, `deadLetterId`, `domain`, `errorClass` e ids de correlação.
- Nenhum componente consome as DLQs do broker; a fonte operacional é a tabela, consultada por API e portal.

```http
GET /v1/dead-letters?domain=&resolved=false&processId=&page=1&pageSize=50
```

```json
{
  "items": [
    {
      "deadLetterId": "dlq_0199a1b2c3d4",
      "processId": "sig_0199a1b2c3d4",
      "operationId": "op_938273",
      "operationType": "PROVIDER_STATUS_CHECK",
      "domain": "signature-provider",
      "queue": "signature-provider-dlq",
      "errorClass": "TRANSIENT",
      "reason": "provider unavailable",
      "attempts": 8,
      "createdAt": "2026-10-06T12:10:00Z",
      "resolvedAt": null
    }
  ],
  "page": 1,
  "pageSize": 50,
  "total": 1
}
```

`domain` aceita `signature-provider`, `artifact`, `identity-proofing`, `callback` e `notification` (outro valor responde 400). Clientes veem só as dead letters dos próprios processos e sessões. A resolução ocorre por reprocessamento (seção 14) ou por reconciliação (seção 18).

> **Estado:** Implementado.
>
> Lacuna menor: a página *Dead letters* do portal não oferece o domínio `notification` no filtro. Specs 003, 010. D-026 (o Worker consome as cinco filas), D-027, D-067, D-076.

---

# 18. Reconciliação

A plataforma deverá possuir um processo periódico de reconciliação.

Objetivo:

detectar diferenças entre o estado interno e o estado registrado no provedor.

Exemplo:

```text
Interno:

SIGNATURE_IN_PROGRESS

Provider:

COMPLETED
```

O reconciliador deverá atualizar o processo e continuar o workflow.

Webhook não deverá ser a única fonte de atualização.

A estratégia será:

```text
Webhook
+
Reconciliation
```

## Reconciliação periódica e manual

- **Candidatos**: processos em `READY_FOR_SIGNATURE`, `SIGNATURE_IN_PROGRESS` ou `PARTIALLY_SIGNED`, com registro no provider e sem conferência há mais que `StaleAfterSeconds` (120 s), do mais antigo ao mais novo, em lotes de 50. O ciclo roda a cada 60 s (`Reconciliation:*`).
- **Correção**: se o provider está à frente (`SIGNED`, `PARTIALLY_SIGNED`, `REJECTED` ou `CANCELLED`), o estado interno é corrigido pela máquina de estados, os signatários assinados são marcados, os callbacks do novo estado são emitidos, as consultas de status obsoletas são canceladas (resolvendo suas dead letters), o estado operacional volta a `READY` e, se assinado, é criada **uma** operação `SIGNED_DOCUMENT_DOWNLOAD`. Provider atrás do estado interno não altera nada.
- **Manual**: `POST /v1/signature-processes/{id}/reconcile` (processo terminal responde 409). Histórico em `GET /v1/signature-processes/{id}/reconciliations`.
- **Gatilhos** (`trigger`): `SCHEDULED`, `MANUAL` e `WEBHOOK`.

```json
{
  "processId": "sig_0199a1b2c3d4",
  "outcome": "CORRECTED",
  "internalStatus": "SIGNATURE_IN_PROGRESS",
  "providerStatus": "SIGNED",
  "corrected": true,
  "resultingStatus": "COMPLETED",
  "error": null
}
```

`outcome`: `CORRECTED`, `CONSISTENT`, `NOT_APPLICABLE` (sem registro no provider ou fora da janela de estados) e `PROVIDER_ERROR` (200 com o erro, sem alterar estado). Status normalizados do provider: `PENDING`, `PARTIALLY_SIGNED`, `SIGNED`, `REJECTED`, `CANCELLED`.

Só correções e chamadas manuais gravam registro de reconciliação; conferências agendadas sem divergência ou com erro do provider ficam apenas em log (D-051). A reconciliação é idempotente e segura sob concorrência (token otimista, até 12 tentativas internas; esgotadas, 409). A reconciliação manual é sempre auditada (`RECONCILIATION_REQUESTED`).

## Webhooks de provider

```http
POST /v1/webhooks/docusign
POST /v1/webhooks/lacuna
```

Rotas públicas (fora do token) e **fora do Swagger**, autenticadas pela assinatura do provedor: DocuSign Connect por HMAC-SHA256 do corpo; Lacuna por segredo compartilhado em tempo constante. Sem a chave configurada, toda chamada é rejeitada. O **payload é só gatilho**: a plataforma consulta o provider e reconcilia (`trigger=WEBHOOK`); o conteúdo do webhook nunca altera estado por si. Respostas: **200** `{ "status": "processed", "outcome": "..." }` ou `{ "status": "ignored" }` (referência desconhecida ou processo terminal; nada vaza); **401** assinatura ausente ou inválida; **404** provider sem handler; **413** corpo acima de 1 MiB. Um webhook gera o evento `PROVIDER_WEBHOOK_RECEIVED`.

> **Estado:** Implementado parcialmente.
>
> - **Implementado:** periódica, manual, por webhook, correção completa, auditoria.
> - **Falta:** (a) os webhooks não têm proteção contra replay (nem timestamp nem nonce) e reconciliam de forma síncrona na requisição HTTP; (b) o provider simulado não tem webhook (a reconciliação o cobre); (c) sessões de proofing não são reconciliadas; (d) a integração com os sandboxes reais de DocuSign e Lacuna não foi validada (seção 54).
>
> Specs 006, 011. D-049 a D-055, D-086.

---

# 19. Circuit Breaker

Cada integração externa deverá possuir circuit breaker independente.

Estados:

```text
CLOSED

OPEN

HALF_OPEN
```

O objetivo é impedir tempestades de chamadas quando um fornecedor estiver indisponível.

> **Estado:** Planejado (Fase 2).
>
> Não há circuit breaker no código (nenhuma biblioteca de resiliência de chamadas). A constituição o define como opcional no MVP e obrigatório a partir da Fase 2. Hoje a contenção de uma indisponibilidade de fornecedor vem de retry com backoff e jitter, DLQ, timeouts (10 s em callbacks, 30 s na busca de documento) e reconciliação. A métrica `orchestrator.provider.errors` já permite medir a taxa de erro que alimentaria um disjuntor. Constitution V; seção 47.

---

# 20. Bulkhead

Falhas em um fornecedor não deverão degradar os demais.

Os consumidores deverão possuir isolamento lógico ou físico.

Exemplo:

```text
provider.docusign.commands

provider.clicksign.commands

provider.adobe.commands

identity.commands

callback.commands

artifact.commands
```

> **Estado:** Implementado parcialmente.
>
> - **Existe** isolamento lógico **por domínio**: uma fila, uma DLQ e um consumidor com conexão e canal próprios para `signature-provider`, `artifact`, `identity-proofing`, `callback` e `notification`; `prefetch` de 10 mensagens por consumidor. Uma DLQ de callback, por exemplo, não bloqueia o polling de assinatura (D-033, D-076).
> - **Não existe** isolamento **por fornecedor**: DocuSign, Lacuna e o provider simulado compartilham a fila `signature-provider`, então um fornecedor lento pode ocupar os consumidores dos demais. Filas por fornecedor e limites de concorrência são Fase 2 (seção 47).
> - O rate limit de callbacks por host de destino (10 por segundo) é em memória por instância.
>
> Specs 003, 010. D-026, D-076.

---

# 21. Idempotência

Toda operação que possa ser repetida deverá ser idempotente.

O modelo deverá utilizar:

```text
processId

operationId

idempotencyKey

externalReference
```

Antes de criar uma nova operação em um provider, o adapter deverá verificar se já existe operação equivalente.

## Como foi implementado

| Nível | Mecanismo |
|---|---|
| Criação de processo e de sessão de proofing | Header `Idempotency-Key` obrigatório (até 128 caracteres); hash SHA-256 do JSON canônico do corpo; mesma chave e corpo = 200 com o mesmo recurso; corpo diferente = 409. Retenção de 24 h (`Idempotency:RetentionHours`), aplicada ao reutilizar a chave. Escopo por cliente (`c:`) ou pessoa (`u:`). Cinquenta requisições concorrentes com a mesma chave criam um único processo. |
| Mensagens | Cada mensagem tem o identificador da linha de outbox; o inbox ignora reentregas (seção 23). |
| Operações | Só `NOT_STARTED` e `RETRY_PENDING` executam; as demais são ignoradas. Cada operação tem sequência única por processo e tipo. |
| Provider | Contrato do adapter: idempotência pela `externalReference` (o id do processo). `PROVIDER_CREATE_PROCESS` devolve o recurso existente. No DocuSign, o envelope leva a referência como campo personalizado para impedir duplicidade em retry. |
| Artefatos | Um artefato por processo e tipo; gravar de novo devolve o existente. |
| Callbacks | `eventId` estável entre tentativas e entrega única por operação. |
| Reconciliação e reprocesso | Concorrência resolvida por token otimista (`version`); reprocessos concorrentes são aceitos uma vez. |

> **Estado:** Implementado.
>
> Risco residual declarado: a Lacuna pode duplicar o documento remoto se a conexão cair entre a criação remota e o commit local (seção 54). Specs 001, 003, 005, 011. D-003, D-004, D-014, D-090.

---

# 22. Outbox Pattern

Eventos não deverão ser publicados diretamente após a persistência.

Fluxo obrigatório:

```text
Transaction

signature_process
+
outbox_event

COMMIT

↓

Outbox Publisher

↓

Event Bus
```

Isso deverá garantir consistência entre persistência e publicação.

## Como foi implementado

- A mensagem é gravada em `outbox_event` **na mesma transação** da mudança de estado. A criação do processo grava processo, signatários, confirmações, a primeira operação, a chave de idempotência, o evento `PROCESS_CREATED` e a mensagem em um único `SaveChanges`.
- O **Outbox Publisher** roda no Worker: a cada 300 ms seleciona até 50 mensagens com `available_at` vencido (`FOR UPDATE SKIP LOCKED`, seguro com várias instâncias), publica no RabbitMQ com confirmação do broker e marca como publicada. Falha reabre a conexão e tenta de novo.
- Retries e o polling do provider usam `available_at` futuro na própria outbox.
- A entrega é **at-least-once**: se o lote falha no meio, mensagens já publicadas podem ser republicadas, e o inbox deduplica. Um teste derruba o broker durante o fluxo e confirma que nada se perde (`WorkflowTests.Broker_outage_loses_nothing_and_process_completes_after_recovery`).
- As mensagens carregam só referências (`processId`, `operationId`, ids de correlação e `traceparent`); o teste `WorkflowTests.Broker_messages_carry_only_references` verifica isso.
- Linhas publicadas não são apagadas: não há rotina de limpeza da outbox (seção 54).

> **Estado:** Implementado.
>
> A API não publica no broker; só o Worker roda o publisher. Specs 001, 003. D-011, D-013. Detalhe: [ARCHITECTURE.md](ARCHITECTURE.md).

---

# 23. Inbox Pattern

Workers deverão possuir controle de mensagens processadas.

Cada evento deverá possuir identificador único.

Eventos duplicados deverão ser ignorados de maneira segura.

## Como foi implementado

- Tabela `inbox_message`, com chave `(consumer, message_id)`; o `message_id` é o identificador da linha de outbox, carregado como `message-id` AMQP.
- A gravação do inbox acontece **na mesma transação** que o efeito da operação. Reentrega de mensagem já tratada resulta em `Duplicate`: confirma a mensagem sem nenhum efeito. Violação de unicidade na corrida também resolve como duplicata.
- Reprocessamentos e reagendamentos geram uma nova linha de outbox (novo identificador) e, portanto, não colidem com o inbox.
- Todas as filas usam o mesmo nome de consumidor (`provider-worker`); o identificador da mensagem é globalmente único, então isso não gera colisão.
- Falhas de infraestrutura não gravam inbox e devolvem a mensagem à fila.
- Efeitos externos (gravar objeto no storage, chamar provider, enviar callback) ficam fora da transação; por isso os adapters e o `ArtifactService` são idempotentes (seção 21).
- Sem rotina de limpeza do inbox (seção 54).

> **Estado:** Implementado.
>
> `WorkflowTests.Duplicate_message_delivery_has_no_side_effects`. Specs 001. D-011.

---

# 24. Event Journal

A plataforma deverá possuir um journal append-only.

Eventos possíveis:

```text
PROCESS_CREATED

DOCUMENT_RECEIVED

DOCUMENT_STORED

IDENTITY_VALIDATION_REQUESTED

IDENTITY_VALIDATED

PROVIDER_SELECTED

PROVIDER_REQUEST_SENT

PROVIDER_ACCEPTED

SIGNER_NOTIFIED

SIGNER_OPENED_DOCUMENT

SIGNATURE_STARTED

SIGNATURE_COMPLETED

SIGNED_DOCUMENT_RECEIVED

FINAL_DOCUMENT_STORED

CALLBACK_REQUESTED

CALLBACK_DELIVERED

DOCUMENT_DOWNLOADED
```

Modelo:

```json
{
  "eventId": "evt_123",

  "processId": "sig_456",

  "type": "SIGNATURE_COMPLETED",

  "timestamp": "2026-10-06T02:20:13Z",

  "actor": {
    "type": "PROVIDER",
    "id": "DOCUSIGN"
  },

  "metadata": {},

  "correlationId": "...",

  "causationId": "..."
}
```

## Como foi implementado

A tabela `journal_event` é imposta como **append-only por trigger do banco**: `UPDATE`, `DELETE` e `TRUNCATE` falham com exceção (`JournalTests.Journal_is_append_only`). Consulta paginada por ordem de gravação em `GET /v1/signature-processes/{id}/events`. Para sessões de proofing, `processId` carrega o id da sessão.

**Atores** (`actor.type`): `CONSUMER` (cliente de API), `OPERATOR` (pessoa autenticada no portal ou, com a autenticação desligada, o `X-Operator-Id`), `SYSTEM` (workers, emissor e despachante de callbacks, reconciliador, retenção) e `PROVIDER` (código do fornecedor, por exemplo `SIMULATED`, `DOCUSIGN` ou `LACUNA`). Com a autenticação ligada, o ator vem do token, nunca de header.

**Tipos de evento gravados**

| Grupo | Eventos |
|---|---|
| Processo | `PROCESS_CREATED`, `DOCUMENT_RECEIVED`, `DOCUMENT_STORED`, `PROCESS_COMPLETED`, `PROCESS_CANCELLED`, `SIGNED_DOCUMENT_RECEIVED`, `FINAL_DOCUMENT_STORED` |
| Identidade | `IDENTITY_VALIDATION_REQUESTED`, `IDENTITY_VALIDATED`, `PROOFING_SESSION_CREATED`, `EVIDENCE_RECEIVED`, `PROOFING_SESSION_COMPLETED`, `EVIDENCE_DELETED` |
| Provider | `PROVIDER_SELECTED`, `PROVIDER_REQUEST_SENT`, `PROVIDER_ACCEPTED`, `PROVIDER_SEND_DEFERRED`, `PROVIDER_METADATA_INSPECTED`, `PROVIDER_WEBHOOK_RECEIVED` |
| Assinatura | `SIGNER_NOTIFIED`, `SIGNATURE_STARTED`, `SIGNER_SIGNED`, `SIGNER_RELEASED`, `SIGNATURE_COMPLETED`, `SIGNATURE_REJECTED` |
| Confirmação | `CONFIRMATION_REQUESTED`, `CONFIRMATION_SENT`, `CONFIRMATION_RESEND_REQUESTED`, `CONFIRMATION_CONFIRMED`, `CONFIRMATION_CODE_REJECTED`, `CONFIRMATION_CODE_EXPIRED`, `CONFIRMATION_LOCKED` |
| Resiliência | `OPERATION_RETRY_SCHEDULED`, `OPERATION_FAILED`, `OPERATION_DEAD_LETTERED`, `OPERATION_REPROCESS_REQUESTED` |
| Callback | `CALLBACK_REQUESTED`, `CALLBACK_DELIVERED` |
| Artefatos | `DOWNLOAD_LINK_ISSUED`, `DOCUMENT_DOWNLOADED` |
| Reconciliação | `RECONCILIATION_REQUESTED`, `RECONCILIATION_DISCREPANCY_FOUND`, `RECONCILIATION_CORRECTED`, `RECONCILIATION_PROVIDER_ERROR` |

Diferenças em relação à lista do PRD original: `SIGNER_OPENED_DOCUMENT` **não é gravado** (nenhum provider o informa); **não existe evento de falha de callback** (a falha aparece nos eventos `OPERATION_*`). Os metadados do evento nunca carregam o código de confirmação, nem dados pessoais completos, nem conteúdo de evidência; destinos de confirmação saem mascarados. O `causationId` encadeia cada evento ao anterior da mesma ação.

> **Estado:** Implementado parcialmente.
>
> `SIGNER_OPENED_DOCUMENT` não é emitido. O journal só cresce: não há política de retenção nem arquivamento (seção 54). Specs 001, 003, 004, 006, 007, 010. D-021, D-057, D-059, D-090.

---

# 25. Armazenamento

A plataforma deverá utilizar pelo menos duas classes de armazenamento.

## Transaction Store

Preferência:

**PostgreSQL com JSONB**

Responsável por:

- processos;
- estados;
- operações;
- configurações;
- metadados;
- provider metadata;
- histórico;
- callbacks;
- retries;
- eventos.

## Como foi implementado

PostgreSQL 16 com JSONB para o corpo da requisição, destino de callback, validações de identidade, entrada, saída e erro de operação, metadados do evento, da mensagem de outbox, do artefato e do provider, dados do sujeito de uma sessão de proofing e detalhes de validação. Não há índices GIN: o JSONB guarda payloads, não é consultado por campo.

- **Processos, signatários, confirmações, operações e histórico**: tabelas relacionais (`signature_process`, `signer`, `signer_confirmation`, `operation`, `journal_event`). Retries são atributos da operação (`attempt`, `max_attempts`, `next_retry_at`) mais `dead_letter_entry`.
- **Callbacks**: `callback_registration` (destinos cadastrados) e `callback_delivery` (entregas).
- **Configurações**: ficam em variáveis de ambiente e arquivos `appsettings`, não no banco; a única configuração persistida são os callbacks cadastrados.
- **Concorrência**: token otimista `version` em `signature_process` e `signer_confirmation`; bloqueio de linha na sessão de proofing.
- **Migrações**: aplicadas pela API na inicialização; o Worker espera o esquema.

O modelo de dados completo, com colunas, índices e migrações, está em [ARCHITECTURE.md](ARCHITECTURE.md).

> **Estado:** Implementado.
>
> Specs 001 a 011. D-002, D-010, D-011, D-014.

---

# 26. Artifact Store

Preferência:

**Object Storage compatível com S3.**

Responsável por:

- documento original;
- documento assinado;
- documentos de identificação;
- selfies;
- certificados;
- evidências;
- comprovantes de assinatura;
- arquivos auxiliares.

Estrutura conceitual:

```text
signature-artifacts/

  {processId}/

      input/
          original.pdf

      identity/
          document-front.jpg
          document-back.jpg
          selfie.jpg

      provider/
          completion-certificate.pdf

      output/
          signed.pdf

      evidence/
          evidence.json
          audit.json
```

## Como foi implementado

Bucket único `signature-artifacts`, acessado só pela API S3 (MinIO no compose), de modo que o backend é trocável (D-020). Chaves reais, sem extensão de arquivo:

```text
signature-artifacts/

  {processId}/
      input/original
      output/signed
      evidence/evidence.json

  {sessionId}/
      identity/document-front
      identity/document-back
      identity/selfie

  uploads/{uploadId}
```

Diferenças em relação à estrutura conceitual:

- `provider/completion-certificate.pdf` e `evidence/audit.json` **não existem**. O que o provider entrega como comprovante é guardado como o artefato `EVIDENCE` (no DocuSign, o Certificate of Completion em PDF; no provider simulado, um JSON determinístico), sempre na chave `evidence/evidence.json`, independentemente do tipo de conteúdo.
- Os artefatos de identidade pertencem à **sessão de proofing** (`{sessionId}`), não ao processo de assinatura.
- Documentos enviados antes de existir o processo ficam em `uploads/{uploadId}`.

**Tipos de artefato** (`type`): `ORIGINAL_DOCUMENT`, `SIGNED_DOCUMENT`, `EVIDENCE`, `DOCUMENT_FRONT`, `DOCUMENT_BACK`, `SELFIE`. Há um artefato por tipo e por processo (ou sessão). As chaves de armazenamento nunca são expostas pela API.

```http
GET /v1/signature-processes/{id}/artifacts
```

```json
{
  "items": [
    {
      "artifactId": "art_0199a1b2c3d4",
      "type": "ORIGINAL_DOCUMENT",
      "contentType": "application/pdf",
      "sha256": "<64 caracteres hexadecimais>",
      "size": 828371,
      "fileName": "contrato.pdf",
      "createdAt": "2026-10-06T12:00:05Z"
    }
  ]
}
```

O limite de documento é de 50 MiB (`Artifacts:MaxDocumentBytes`); evidência de proofing, 5 MiB. O conteúdo é lido por inteiro em memória durante a gravação. A integridade é conferida: nenhum artefato é registrado sem o objeto no store, e a ausência do objeto na leitura é falha transitória.

> **Estado:** Implementado parcialmente.
>
> - **Falta:** certificados e auditoria como artefatos próprios; política de ciclo de vida do bucket; criptografia em repouso (delegada à infraestrutura). Original, assinado e evidência de processos e os uploads **não são apagados** (só as evidências de proofing têm retenção; seção 38).
> - Specs 002, 005, 010, 011. D-017, D-020, D-045, D-080.

---

# 27. Integridade dos Artefatos

Todos os artefatos deverão possuir hash criptográfico.

Preferencialmente:

```text
SHA-256
```

Modelo:

```json
{
  "artifactId": "art_123",

  "type": "SIGNED_DOCUMENT",

  "contentType": "application/pdf",

  "sha256": "f94a89...",

  "size": 828371
}
```

## Como foi implementado

O `sha256` (hexadecimal minúsculo, 64 caracteres) é calculado sobre o conteúdo no momento da gravação e guardado junto com `size`, `contentType` e `fileName`. O download devolve o hash no header `X-Content-SHA256`; os testes conferem que o conteúdo baixado tem o hash listado. Uploads também registram o hash. O hash de evidências de proofing permanece no journal depois da exclusão do conteúdo (`EVIDENCE_DELETED`).

> **Estado:** Implementado.
>
> `ArtifactServiceTests.Stores_object_and_records_sha256_size_and_content_type`, `DownloadTests.Default_link_is_the_signed_document_and_content_matches_the_listed_hash`. Spec 002. D-016, D-017.

---

# 28. Provider Metadata

Metadados nativos dos fornecedores deverão ser preservados.

Exemplo:

```json
{
  "provider": "DOCUSIGN",

  "providerProcessId": "...",

  "normalizedStatus": "SIGNED",

  "metadata": {
    "envelopeId": "...",
    "recipientId": "...",
    "authenticationMethod": "...",
    "providerStatus": "...",
    "timestamps": {}
  }
}
```

Esses dados poderão ser armazenados utilizando JSONB.

## Como foi implementado

A tabela `provider_process` guarda, por processo, o código do provider (`SIMULATED`, `DOCUSIGN` ou `LACUNA`), o id do processo no provider, a referência externa (o id do processo interno), o status normalizado e os metadados nativos em JSONB.

```http
GET /v1/signature-processes/{id}/provider        (operator, client, admin; viewer não)
```

```json
{
  "provider": "DOCUSIGN",
  "providerProcessId": "<id do envelope>",
  "externalReference": "sig_0199a1b2c3d4",
  "normalizedStatus": "SIGNED",
  "metadata": { "envelopeId": "<id do envelope>", "status": "completed", "signers": 2 },
  "updatedAt": "2026-10-06T12:05:00Z"
}
```

Os metadados reais são menores que os do exemplo original: no DocuSign guardam o `envelopeId`, o status nativo e a contagem de signatários; `recipientId`, método de autenticação e linha do tempo do provider **não** são preservados. Status normalizados: `PENDING`, `PARTIALLY_SIGNED`, `SIGNED`, `REJECTED`, `CANCELLED`. **Ler os metadados é uma ação auditada** (`PROVIDER_METADATA_INSPECTED`).

> **Estado:** Implementado parcialmente.
>
> Os campos nativos preservados são os mínimos; ampliá-los é trabalho futuro. Specs 001, 007, 011. D-059.

---

# 29. Provider Adapters

A plataforma deverá utilizar Adapter Pattern.

```text
Signature Orchestrator

        ↓

Provider Router

        ↓

Provider Adapter

 ┌──────┼──────────┐

DocuSign

Clicksign

Adobe Sign

D4Sign
```

Cada adapter deverá implementar uma interface comum.

Conceitualmente:

```text
createProcess()

sendDocument()

addSigner()

getStatus()

cancel()

downloadSignedDocument()

downloadEvidence()
```

## Como foi implementado

A interface `IProviderAdapter` tem: `Code`, `CreateProcessAsync`, `SendDocumentAsync`, `AddSignerAsync`, `ReleaseSignerAsync`, `SupportsPerSignerReleaseAsync`, `GetStatusAsync`, `CancelAsync`, `DownloadSignedDocumentAsync` e `DownloadEvidenceAsync`. Todos os métodos são idempotentes pela referência externa. O contrato não expõe tipo algum de fornecedor. `AddSignerAsync` existe na interface, mas o fluxo atual não o chama (os signatários vão em `CreateProcessAsync`). Um decorador mede latência e erros de cada chamada e cria spans.

| Adapter | Estado | Observações |
|---|---|---|
| Simulado (`SIMULATED`) | Implementado, **padrão** | Assina automaticamente com atraso configurável; marcadores no `externalId` simulam falhas: `SIM-FLAKY-n-`, `SIM-DOWN-`, `SIM-UNKNOWN-`, `SIM-FAIL-`, `SIM-REJECT-`. Libera signatário a signatário. |
| DocuSign | Implementado em sandbox, **não validado** em ambiente real | OAuth JWT Grant (RS256); envelope criado em rascunho e enviado ao final; `routingOrder` vem de `order`; webhook Connect assinado. |
| Lacuna Signer | Implementado em sandbox, **não validado** em ambiente real | Autenticação por chave de API; o fluxo remoto nasce no envio do documento; webhook por segredo compartilhado. |
| Clicksign, Adobe Sign, D4Sign | Planejado | Citados no PRD original; nenhum código. |

**Provider Router.** O provider de um processo novo vem de `Provider:Default` (`Simulated`, `DocuSign` ou `Lacuna`; padrão `Simulated`). A partir daí, toda chamada segue o código gravado em `provider_process`, de modo que trocar a configuração **não afeta processos em andamento**. Não há roteamento por regra, custo ou risco, nem fallback automático (Fase 2 e 3).

**Credenciais** só por variáveis de ambiente (`DOCUSIGN_*`, `LACUNA_*`), com carga de `.env` fora do controle de versão; mensagens de erro citam o nome da variável ausente, nunca valores. Credencial ausente só falha quando o provider correspondente é selecionado.

**Sem liberação por signatário.** DocuSign e Lacuna não oferecem liberação individual; a plataforma segura o envio até todos os canais de todos os signatários estarem confirmados (seção 6.3). Assinatura avançada ou qualificada específica de cada fornecedor não é configurada.

> **Estado:** Implementado parcialmente.
>
> Três adapters, sendo dois reais só exercitados contra servidores falsos em processo; os testes `Live*` contra sandboxes ficam pulados sem credenciais (seção 54). Specs 001, 006, 011. D-005, D-082 a D-088.

---

# 30. Artifact Delivery

Ao final do processo, a plataforma deverá manter o documento final internamente.

Quando configurado, deverá também enviar ou disponibilizar o documento para o destino indicado pelo consumidor.

O armazenamento externo nunca deverá ser considerado a única cópia existente.

> **Estado:** Implementado parcialmente.
>
> - **Implementado:** a plataforma mantém internamente original, assinado e evidência (seção 26) e os disponibiliza por link temporário (seção 31). O callback de conclusão já traz o `downloadUrl` do documento assinado (seção 7).
> - **Planejado:** o envio ativo a um destino indicado pelo consumidor (`output.destination` e a operação `OUTPUT_DELIVERY`) **nunca foi construído**: o campo é ignorado pela API e a operação não tem executor. Foi adiado na spec 002 (D-018) e nenhuma spec posterior o retomou.
>
> Specs 002. D-018.

---

# 31. Download Seguro

O sistema consumidor deverá conseguir obter o documento final através de URL temporária.

Exemplo:

```http
POST /v1/signature-processes/{id}/download-link
```

Resposta:

```json
{
  "url": "https://documents.exemplo.com/...",

  "expiresAt": "2026-10-06T03:00:00Z"
}
```

Características:

```text
URL assinada

Expiração curta

Escopo para um único artefato

Auditoria

Revogação quando aplicável

Single use opcional
```

Não deverão existir chaves permanentes expostas em URLs.

## Como foi implementado

Corpo opcional do `POST`: `{ "artifactId": "art_..." }` ou `{ "type": "SIGNED_DOCUMENT" }`; precedência `artifactId`, depois `type`. Sem corpo: documento assinado se o processo está `COMPLETED`, senão o original. Artefato inexistente responde 404; `type` desconhecido, 400. Papéis `operator`, `client` e `admin`.

```json
{
  "url": "https://api.exemplo.com/v1/downloads/art_0199a1b2c3d4?expires=1790000000&sig=<hmac-base64url>",
  "expiresAt": "2026-10-06T12:05:00Z"
}
```

```http
GET /v1/downloads/{artifactId}?expires=<unix>&sig=<assinatura>
```

Rota pública, autorizada só pela assinatura: `sig` é o HMAC-SHA256 de `{artifactId}.{expires}`, verificado em tempo constante. Sucesso: 200 com o binário, `Content-Type`, `Content-Length`, `X-Content-SHA256` e `Content-Disposition: attachment`. Assinatura inválida: **403** `Invalid download link`; vencido: **410** `Download link expired`; artefato inexistente: **404**.

| Característica | Estado |
|---|---|
| URL assinada | Implementado |
| Expiração curta | Implementado (300 s por padrão, `Artifacts:LinkTtlSeconds`) |
| Escopo para um único artefato | Implementado (a assinatura vincula o `artifactId`) |
| Auditoria | Implementado (`DOWNLOAD_LINK_ISSUED` na emissão e `DOCUMENT_DOWNLOADED` no download, com ator e hash) |
| Sem chaves permanentes na URL | Implementado (a URL usa o id do artefato, nunca a chave do store nem credencial) |
| Revogação | **Planejado**: não há revogação; o controle é só a expiração |
| Single use opcional | **Planejado**: o link vale para vários downloads até expirar (D-016) |

A base da URL vem de `Artifacts:PublicBaseUrl` (padrão `http://localhost:8080`, só desenvolvimento); fora do ambiente local é preciso configurá-la (seção 54). Evidências de proofing nunca são baixáveis.

> **Estado:** Implementado parcialmente (revogação e uso único pendentes).
>
> Specs 002, 009. D-016, D-045, D-061, D-067.

---

# 32. Arquitetura de Processamento

Fluxo:

```text
                     Sistemas Consumidores
                              │
                              ▼
                         API Gateway
                              │
                              ▼
                        Signature API
                              │
                    Transaction + Outbox
                              │
              ┌───────────────┼───────────────┐
              │                               │
              ▼                               ▼
         PostgreSQL                       Event Bus
          + JSONB                       / Queue / Topic
                                              │
                          ┌───────────────────┼───────────────────┐
                          │                   │                   │
                          ▼                   ▼                   ▼
                    Provider Worker     Artifact Worker    Callback Worker
                          │
                          ▼
                    Provider Adapter
                          │
                ┌─────────┼─────────┐
                ▼         ▼         ▼
            DocuSign   Clicksign   Adobe
```

## Como foi implementado

| Componente da visão | Na solução |
|---|---|
| API Gateway | Fora da solução (infraestrutura externa). |
| Signature API | Projeto `Orchestrator.Api` (.NET 8, Minimal API). Não publica nem consome mensagens. |
| PostgreSQL + JSONB | PostgreSQL 16. |
| Event Bus / Queue | RabbitMQ 3.13; uma fila de comando e uma DLQ por domínio. |
| Provider, Artifact e Callback Workers | Consumidores por fila dentro do **único** projeto `Orchestrator.Worker`; o Worker consome por padrão as cinco filas (`signature-provider`, `artifact`, `callback`, `identity-proofing`, `notification`). |
| Provider Adapter | Simulado, DocuSign e Lacuna (seção 29). Clicksign e Adobe Sign: planejado. |

A API grava estado e outbox na mesma transação; o Worker publica o outbox, consome as filas e executa o motor de workflow. O detalhe (projetos, fluxo de execução, filas, DLQs, configuração, docker-compose e portas) está em [ARCHITECTURE.md](ARCHITECTURE.md).

> **Estado:** Implementado.
>
> Specs 001, 003. D-006, D-010, D-011, D-026.

---

# 33. Reconciliation Worker

Deverá existir componente independente:

```text
Reconciliation Worker
        │
        ▼
Busca processos potencialmente inconsistentes
        │
        ▼
Consulta provider
        │
        ▼
Compara estados
        │
        ▼
Atualiza State Machine
        │
        ▼
Continua workflow
```

## Como foi implementado

O reconciliador é um serviço de segundo plano (`ReconciliationWorker`) hospedado no processo do Worker, desligável por instância (`Reconciliation:Enabled`) e com intervalo, obsolescência e lote configuráveis (padrão 60 s, 120 s e 50). Cada processo candidato é reconciliado em escopo próprio, e a falha de um não impede os demais. Várias instâncias do Worker podem rodar juntas: a correção é segura sob concorrência pelo token otimista, e testes comprovam que dez reconciliações simultâneas de um processo divergente geram uma única correção, um único download e um único callback. O algoritmo de comparação e correção está descrito na seção 18.

> **Estado:** Implementado parcialmente.
>
> "Componente independente" foi atendido como serviço desacoplado dentro do Worker, não como processo próprio. Specs 006. D-049, D-050, D-054, D-055.

---

# 34. Operations Portal

A plataforma deverá possuir interface web operacional.

Objetivo:

permitir acompanhamento completo dos processos.

Tela inicial:

| Processo | Documento | Provider | Assinantes | Status | Criado | SLA |
|---|---|---|---|---|---|---|
| 93822 | Contrato.pdf | DocuSign | 2/2 | Completed | 23:41 | OK |
| 93823 | Termo.pdf | Clicksign | 1/2 | Signing | 23:42 | OK |
| 93824 | CCB.pdf | DocuSign | 0/1 | Failed | 23:43 | ALERT |

## Como foi implementado

Portal em `portal/` (React 18, Vite 5 e TypeScript, CSS próprio, sem biblioteca de componentes), servido por nginx na porta 3000 com proxy de `/v1` para a API. Rotas: `/` (lista), `/processes/new` (novo processo), `/processes/:id?tab=` (detalhe), `/dead-letters` e `/auth/callback` (fim do login).

**Lista de processos.** Colunas reais, em ordem: **Process** (id e `externalId`), **Document**, **Provider**, **Signers** (assinados/total), **Etapas** (progresso X/Y com barra; a etapa atual aparece ao passar o mouse), **Status**, **Operational**, **Created** e **SLA**. As colunas *Etapas* e *Operational* foram acrescentadas à tela do PRD original.

| Process | Document | Provider | Signers | Etapas | Status | Operational | Created | SLA |
|---|---|---|---|---|---|---|---|---|
| sig_0199a1b2 | contrato.pdf | DOCUSIGN | 2/2 | 8/8 | COMPLETED | READY | 23:41 | OK |
| sig_0199a1c3 | termo.pdf | SIMULATED | 1/2 | 4/7 | SIGNATURE_IN_PROGRESS | READY | 23:42 | OK |
| sig_0199a1d4 | ccb.pdf | LACUNA | 0/1 | 2/5 | SIGNATURE_IN_PROGRESS | DLQ | 23:43 | ALERT |

- Filtros por status de negócio e operacional, busca por parte do id do processo ou do `externalId`, paginação de 20 por página e atualização automática opcional a cada 10 s (desligada por padrão). O estado dos filtros fica na URL.
- **SLA** é um indicador simples: `ALERT` se o processo está `FAILED`, `REJECTED` ou `EXPIRED`, em `DLQ` ou `MANUAL_ACTION`, ou não terminal há mais de 60 minutos (`Sla:SigningMinutes`); caso contrário `OK`.
- **Etapas (X/Y)** vêm de `progress` na lista: `completedSteps`, `totalSteps` e `currentStep` (seção 35).
- Botão **Novo processo** (próximo item), visível a quem pode criar.

**Novo processo.** Formulário em português: identificador externo, documento em PDF (upload), tipo de assinatura padrão, canais de confirmação por código e tabela de signatários (nome, CPF, e-mail, telefone, ordem). Fluxo: `POST /v1/document-uploads` e depois `POST /v1/signature-processes` com `source` do tipo `UPLOAD` e `defaults`. O upload é reaproveitado enquanto o arquivo é o mesmo e a `Idempotency-Key`, enquanto o corpo é idêntico, o que torna o reenvio seguro. Erros da API aparecem no signatário e campo (`signers[i].campo`). O formulário **não** tem campos de callback nem de validações de identidade. Podem criar processos `client`, `operator` e `admin`; `viewer` não vê o botão e, se abrir o endereço, vê uma explicação (a API continua sendo quem decide, com 403).

**Página Dead letters.** Lista com filtros por domínio (`signature-provider`, `artifact`, `identity-proofing`, `callback`) e por pendente/resolvida, paginada, com link de cada linha para a aba *Errors* do processo. Somente leitura. O domínio `notification` não consta do filtro.

**Idioma.** Misto: os rótulos do PRD (por exemplo *Overview*, *Operations*) estão em inglês e o que veio da spec 010 (*Etapas*, *Novo processo*) em português (D-081). Acessibilidade básica: atalho para o conteúdo, navegação por teclado, tabelas com legenda, abas WAI-ARIA, diálogos modais e layout para telas estreitas.

> **Estado:** Implementado.
>
> Testes do portal: `ProcessList.test.tsx`, `Progress.test.tsx`, `NewProcess.test.tsx`, `CreateAccess.test.tsx`, `DeadLetters.test.tsx`; API de apoio em `PortalApiTests`. Specs 007, 010. D-056, D-058, D-061, D-081, D-089, D-092.

---

# 35. Detalhes do Processo

O portal deverá mostrar:

```text
Overview

Signers

Identity Proofing

Operations

Artifacts

Provider Metadata

Audit Trail

Callbacks

Errors
```

Timeline:

```text
23:41 Process created

23:41 Original document stored

23:41 Identity proofing started

23:42 Identity verified

23:42 Sent to provider

23:44 Signer opened

23:46 Signed

23:46 Final document received

23:47 Callback delivered
```

## Como foi implementado

O detalhe tem **dez abas**, as nove do PRD mais *Etapas* (spec 010), com a aba escolhida na URL (`?tab=`):

| Aba | Conteúdo |
|---|---|
| Overview | Ids, documento, provider, tipo de assinatura, estados de negócio e operacional, SLA, assinados/total, datas, destino de callback e id de correlação. |
| Signers | Nome, identificador externo, documento mascarado, se assinou e quando. |
| Etapas | "X/Y etapas concluídas" e etapa atual; etapas agrupadas por processo, por signatário (confirmação por canal e assinatura) e conclusão, cada uma com seu estado (pendente, em andamento, concluída, falhou, cancelada); para a confirmação, o estado do canal (aguardando a vez, enviando, código enviado, expirado, confirmado, bloqueado), tentativas restantes e envios. |
| Identity Proofing | **Somente as capabilities solicitadas** no processo (obrigatória ou opcional). Não mostra resultado de sessão de proofing. |
| Operations | Tipo, estado, tentativas (`attempt/maxAttempts`), próximo retry e erro; ação *Retry Operation* por linha. |
| Artifacts | Tipo, arquivo, content type, tamanho, SHA-256 e data; ação *Download Artifact*. |
| Provider Metadata | Só depois de *Inspect Provider Metadata* (ação auditada): provider, id remoto, referência, status normalizado e JSON nativo. |
| Audit Trail | Hora, evento, ator, ids de correlação e causação e metadados; "Load more events" (50 por vez). |
| Callbacks | Evento, destino, estado, tentativas, última resposta e erro; ação *Retry Callback*. |
| Errors | Operações com problema, dead letters (pendentes e resolvidas) e eventos de falha. |

A **timeline** é uma lista ordenada com hora, rótulo legível (cerca de 32 tipos de evento traduzidos; tipo desconhecido aparece cru) e ator. Os eventos reais diferem do exemplo do PRD: não há "Signer opened" (seção 24).

**Progresso em etapas.** O plano é derivado dos fatos do processo, sem tabela de etapas: *Documento recebido*; para cada signatário, uma etapa de **confirmação por canal** e a **assinatura**; *Documento final*; e *Callback* (só se o processo tem callback). O total é `2 + signatários + confirmações (+1 com callback)`. Uma etapa conta como concluída quando: o original está armazenado; o canal foi `CONFIRMED`; o signatário assinou; o processo está `COMPLETED`; o callback `COMPLETED` foi entregue. A etapa atual é a primeira não concluída. Em processo fechado, `currentStep` é nulo; etapas não concluídas viram `CANCELLED` (ou `FAILED` na que falhou, em confirmação bloqueada, em rejeição ou em callback com falha) e nunca contam como feitas. O `GET` do processo devolve `progress` com `steps`; a lista devolve o resumo sem `steps`.

```json
{
  "progress": {
    "completedSteps": 3,
    "totalSteps": 7,
    "currentStep": {
      "order": 4,
      "kind": "CONFIRMATION",
      "signerId": "sgn_0199a2",
      "channel": "EMAIL",
      "label": "Confirmação por e-mail - João Exemplo",
      "status": "IN_PROGRESS"
    }
  }
}
```

> **Estado:** Implementado parcialmente.
>
> A aba *Identity Proofing* é limitada (só as capabilities pedidas). O portal não mostra o `correlationId` das respostas de erro. Specs 007, 010. D-078, D-081.

---

# 36. Operações Manuais

Usuários autorizados deverão poder executar:

```text
Retry Operation

Retry Callback

Reprocess From Operation

Reconcile Provider

Download Artifact

Cancel Process

Inspect Provider Metadata
```

Alterações manuais deverão sempre gerar eventos de auditoria.

## Como foi implementado

| Operação no portal | Chamada | Auditoria (evento do journal) |
|---|---|---|
| Retry Operation | `POST /v1/signature-processes/{id}/retry` com `operationId` | `OPERATION_REPROCESS_REQUESTED` |
| Retry Callback | `POST .../retry` com o `operationId` da entrega | `OPERATION_REPROCESS_REQUESTED` |
| Reprocess From Operation | `POST .../retry` (escolhe a operação) | `OPERATION_REPROCESS_REQUESTED` |
| Reconcile Provider | `POST .../reconcile` | `RECONCILIATION_REQUESTED` (e `RECONCILIATION_CORRECTED`, se corrigir) |
| Download Artifact | `POST .../download-link` e download | `DOWNLOAD_LINK_ISSUED`, `DOCUMENT_DOWNLOADED` |
| Cancel Process | `POST .../cancel` | `PROCESS_CANCELLED` |
| Inspect Provider Metadata | `GET .../provider` | `PROVIDER_METADATA_INSPECTED` |

Toda ação pede confirmação em diálogo, aceita motivo opcional (até 500 caracteres) em reprocessos e é desabilitada, com a razão no texto, quando não se aplica (processo terminal, nada a reprocessar). O ator do evento é a pessoa autenticada (`OPERATOR` com o nome de usuário do token); com a autenticação desligada, o operador informado no portal vai em `X-Operator-Id` (1 a 100 caracteres de `A-Z a-z 0-9 . _ @ : -`; outro valor responde 400). No portal, `operator` e `admin` operam; `viewer` só lê; `client` pode criar processos, mas não opera pelo portal. Na API, `client` opera nos próprios processos.

> **Estado:** Implementado.
>
> Testes: `ManualOperations.test.tsx`, `ActorTests.*`, `PortalApiTests.Provider_metadata_are_returned_and_the_access_is_audited_with_the_operator`, `PortalApiTests.Manual_reconciliation_is_always_audited_even_when_consistent`. Specs 007, 008. D-057, D-059, D-062, D-090.

---

# 37. Segurança

A solução deverá possuir:

- OAuth2/OIDC;
- autenticação machine-to-machine;
- RBAC;
- criptografia em trânsito;
- criptografia em repouso;
- KMS;
- Secrets Manager;
- WAF;
- Rate Limiting;
- proteção contra replay;
- segregação por cliente;
- proteção contra SSRF;
- controle de egress;
- mascaramento de informações sensíveis;
- trilha de auditoria;
- política de retenção;
- observabilidade de segurança.

## Como foi implementado

| Requisito | Estado | Como |
|---|---|---|
| OAuth2/OIDC | Implementado | JWT Bearer validado contra o emissor OIDC (Keycloak 25 no compose): emissor, audiência, expiração, tolerância de 30 s. Habilitado por `Auth:Enabled`, que é **falso por padrão no código** (API aberta, ator por `X-Operator-Id`) e verdadeiro no compose. Em qualquer ambiente real deve ser ligado. |
| Machine-to-machine | Implementado | Client credentials para os clientes de API (`orchestrator-client-a` e `-b` no realm de demonstração). |
| RBAC | Implementado | Papéis `viewer`, `operator`, `client` e `admin`, lidos de `realm_access.roles`; matriz abaixo. |
| Criptografia em trânsito | Planejado (infraestrutura) | A aplicação não termina TLS; o compose usa HTTP. Delegado ao ambiente de produção. |
| Criptografia em repouso | Planejado (infraestrutura) | Não configurada pelo código (banco e object storage). |
| KMS / Secrets Manager | Planejado (infraestrutura) | Segredos vêm de variáveis de ambiente e `.env` (fora do git). Segredos de demonstração do repositório e do compose servem só para desenvolvimento. |
| WAF | Planejado (infraestrutura) | Fora do repositório. |
| Rate limiting | Implementado parcialmente | Criação de processo: 600 por minuto por cliente (`RateLimit:CreatePerMinute`), janela fixa em memória por instância, 429 sem `Retry-After`. Reenvio de código: 30 s e 5 envios por canal. Callbacks: 10 por segundo por host. Nada nos demais endpoints. |
| Proteção contra replay | Implementado parcialmente | Callbacks têm timestamp assinado e tolerância de 300 s para o receptor; links de download expiram; códigos de confirmação são de uso único. **Webhooks de provider não têm** proteção contra replay. |
| Segregação por cliente | Implementado | Ver abaixo. |
| Proteção contra SSRF | Implementado parcialmente | Callbacks: completo (seção 8). Busca do documento por URL: guarda disponível, **desligada por padrão**. |
| Controle de egress | Planejado (infraestrutura) | Fora da aplicação. |
| Mascaramento | Implementado | CPF e telefone com os 2 últimos dígitos; e-mail como `m***@dominio`; logs e respostas; evidência nunca registrada. |
| Trilha de auditoria | Implementado | Seção 24; o ator vem do token. |
| Política de retenção | Implementado parcialmente | Só evidências de proofing (30 dias). Documentos, uploads, outbox, inbox e journal não têm retenção. |
| Observabilidade de segurança | Implementado parcialmente | Falhas de autenticação e autorização são logadas e contadas em `orchestrator.security.denied`; não há alertas. |

**Matriz de papéis** (com `Auth:Enabled=true`):

| Grupo de rotas | viewer | operator | client | admin |
|---|---|---|---|---|
| Leituras (`GET`) | sim | sim | sim | sim |
| `GET .../provider` | não | sim | sim | sim |
| `GET /v1/dev/*` (códigos de confirmação simulados) | não | não | não | sim |
| `POST /v1/signature-processes`, `POST /v1/document-uploads` | não | sim | sim | sim |
| `POST` e `DELETE /v1/callbacks` | não | não | sim | sim |
| `POST /v1/proofing-sessions`, `/documents`, `/biometrics` | não | não | sim | sim |
| `retry` e `DELETE .../evidence` de proofing | não | sim | sim | sim |
| Demais escritas (cancelar, reprocessar, reconciliar, confirmar, reenviar código, gerar link) | não | sim | sim | sim |
| `/health`, `/swagger`, `/v1/downloads`, `/v1/webhooks` | públicas | públicas | públicas | públicas |

**Segregação por cliente.** Um "cliente de máquina" é quem tem o papel `client` sem `operator` nem `admin`; seu identificador é o `azp` do token. Processos, sessões de proofing, callbacks cadastrados, uploads e dead letters guardam o dono; artefatos herdam o dono do processo. Recurso alheio responde **404** (nunca 403), em rota e em listagem, para não revelar existência. Callbacks de `admin` (sem dono) servem a qualquer cliente, mas não aparecem na listagem dele. `operator`, `viewer` e `admin` veem tudo.

**Processos criados por pessoas** (portal) não têm cliente dono: ficam invisíveis a clientes de API e visíveis à operação. A `Idempotency-Key` é escopada por usuário, o ator é `OPERATOR` com o `preferred_username` do token, e upload feito por pessoa só serve a pessoas (D-090).

**Login do portal.** Authorization code com **PKCE S256** contra o Keycloak (client público `portal`, sem password grant): `state` validado, verifier e `state` de uso único, redirecionamento pós-login só para caminho interno. O endpoint de token passa pelo proxy nginx; o de autorização é acessado direto no IdP (fora de `localhost`, exige `VITE_AUTH_URL`). A sessão (token, usuário, papéis, refresh token) fica em `sessionStorage`.

**Renovação do token.** O access token vale 15 minutos. O portal renova com o refresh token quando faltam menos de 30 s, em segundo plano cerca de 60 s antes de expirar, e uma vez ao receber 401 (repetindo a chamada); renovações simultâneas compartilham uma só. Refresh recusado encerra a sessão e pede login; IdP fora do ar mantém a sessão enquanto o token valer (D-091). O logout limpa a sessão local, mas **não encerra a sessão no Keycloak**. Para scripts há o client `orchestrator-cli` (password grant, só desenvolvimento).

> **Estado:** Implementado parcialmente.
>
> O que é infraestrutura de produção (TLS, criptografia em repouso, KMS, secrets manager, WAF, egress) não faz parte do repositório; ver limitações na seção 54. Testes: `AuthApiTests.*`, `RbacUnitTests.Matrix`, `SegregationTests.*`, `HumanCreationTests.*`, `Auth.test.tsx`, `TokenRefresh.test.tsx`. Specs 008, 009, 011 e correção posterior. D-062 a D-070, D-083, D-086, D-089 a D-092.

---

# 38. Proteção de Dados

A plataforma poderá processar:

- CPF;
- RG;
- CNH;
- CIN;
- endereço;
- telefone;
- e-mail;
- selfie;
- biometria;
- documentos contratuais.

Dados biométricos deverão ser classificados como dados pessoais sensíveis.

A solução deverá possuir políticas explícitas de:

```text
Retention

Deletion

Encryption

Access Control

Audit

Data Minimization

Purpose Limitation
```

## Como foi implementado

**O que a solução trata hoje**: CPF, nome, e-mail e telefone dos signatários; documentos contratuais; imagens de documento (frente e verso) e selfie em sessões de proofing; código de confirmação (apenas o HMAC). RG, CNH, CIN e endereço não são tratados como dados estruturados: só existem como imagem de evidência.

A política escrita está em [DATA-PROTECTION.md](DATA-PROTECTION.md). Situação de cada política:

| Política | Estado | Como |
|---|---|---|
| Retention | Implementado parcialmente | Evidências de proofing: 30 dias após a conclusão (`Identity:EvidenceRetentionDays`), com varredura horária. Sem retenção para documentos, uploads, journal, outbox e inbox. |
| Deletion | Implementado parcialmente | `DELETE /v1/proofing-sessions/{id}/evidence` em sessão concluída; objetos e registros são removidos e os hashes ficam no journal. Não há exclusão de documentos de processos. |
| Encryption | Planejado (infraestrutura) | Em trânsito e em repouso, delegado ao ambiente. Códigos de confirmação são guardados só como HMAC. |
| Access Control | Implementado | Evidências **não são baixáveis** (só metadados e hash); RBAC e segregação por cliente. |
| Audit | Implementado | Criação, evidências com hash, resultados, exclusões e reprocessos no journal. |
| Data Minimization | Implementado | Filas e DLQ só com identificadores; detalhes de validação sem dados pessoais; respostas mascaradas; códigos e evidências nunca em log, evento ou mensagem. |
| Purpose Limitation | Implementado | Evidências servem só às validações da própria sessão. |

Lacunas conhecidas: `DATA-PROTECTION.md` ainda não cobre os dados de contato dos signatários, os códigos de confirmação (a decisão está em D-074 e D-079) nem os uploads (D-080); segredos de callback cadastrados ficam em texto no banco, exibidos uma única vez na criação; a classificação de dados biométricos como sensíveis está só na política, sem controle técnico adicional além dos acima.

> **Estado:** Implementado parcialmente.
>
> Specs 005, 010. D-045, D-048, D-074, D-079, D-080.

---

# 39. Observabilidade

Todos os componentes deverão implementar observabilidade.

Preferência:

**OpenTelemetry**

Deverão existir:

```text
Logs

Metrics

Traces

Correlation ID

Process ID

Operation ID

Provider ID
```

Um processo deverá poder ser rastreado ponta a ponta.

## Como foi implementado

- **OpenTelemetry** na API (`orchestrator-api`) e no Worker (`orchestrator-worker`), com exportação OTLP por HTTP a um OpenTelemetry Collector (`OTEL_EXPORTER_OTLP_ENDPOINT`; sem endpoint, não exporta).
- **Traces**: spans `operation.receive`, `operation.execute` e `provider.<chamada>`, além dos spans HTTP. Tags `correlation.id`, `process.id`, `operation.id`, `operation.type` e `provider.id`. O `traceparent` é gravado na mensagem de outbox e retomado no consumidor, de modo que **um único trace liga a requisição da API e as operações do Worker** (`TelemetryTests.One_trace_connects_the_request_and_the_worker_operations_with_correlation_tags`).
- **Logs** estruturados (Serilog) com `TraceId`, `SpanId`, `ProcessId`, `OperationId` e `CorrelationId`, também exportados por OTLP.
- **Correlation ID**: `X-Correlation-Id` aceito ou gerado, devolvido em toda resposta, gravado no journal e nas mensagens, e presente nos erros.
- **Métricas**: seção 40.
- `/health` verifica apenas a conexão com o PostgreSQL.

O coletor exporta **métricas** para um endpoint Prometheus na porta 8889; **traces e logs** vão apenas para o `debug` do coletor (saída padrão). Não há backend de traces nem de logs, nem Prometheus ou Grafana no compose, e não há tracing de banco nem do cliente AMQP.

> **Estado:** Implementado parcialmente.
>
> Specs 008. D-065.

---

# 40. Métricas

Métricas mínimas:

| Métrica | Objetivo |
|---|---|
| Process Created | Volume |
| Process Completed | Volume |
| Process Failed | Qualidade |
| Average Completion Time | Performance |
| Provider Latency | Performance |
| Provider Error Rate | Disponibilidade |
| Retry Rate | Resiliência |
| DLQ Size | Operação |
| Callback Success Rate | Integração |
| Reconciliation Rate | Consistência |
| Identity Proofing Failure | Fraude/UX |
| Signature Conversion | Negócio |

## Como foi implementado

Medidor OpenTelemetry `Orchestrator`. Os nomes abaixo são os dos instrumentos; o exportador Prometheus do coletor normaliza os pontos para sublinhado.

| Métrica do PRD | Instrumento | Tipo | Marcadores | Estado |
|---|---|---|---|---|
| Process Created | `orchestrator.process.created` | contador | | Implementado |
| Process Completed | `orchestrator.process.completed` | contador | | Implementado |
| Process Failed | `orchestrator.process.failed` | contador | `status` | Implementado parcialmente: conta `FAILED`, `REJECTED` e `EXPIRED`, mas só `REJECTED` ocorre na prática (seção 11) |
| Average Completion Time | `orchestrator.process.completion_time` | histograma (s) | | Implementado |
| Provider Latency | `orchestrator.provider.latency` | histograma (ms) | `provider`, `call` | Implementado |
| Provider Error Rate | `orchestrator.provider.errors` | contador | `provider`, `call`, `transient` | Implementado (a taxa se obtém dividindo pelas chamadas do histograma de latência) |
| Retry Rate | `orchestrator.operation.retries` | contador | `operation.type` | Implementado |
| DLQ Size | `orchestrator.deadletter.size` | medidor | | Implementado (calculado da tabela a cada 10 s) |
| Callback Success Rate | `orchestrator.callback.deliveries` | contador | `result` (`delivered` ou `failed`) | Implementado |
| Reconciliation Rate | `orchestrator.reconciliation.runs` | contador | `outcome`, `trigger` | Implementado |
| Identity Proofing Failure | `orchestrator.proofing.failures` | contador | `capability` | Implementado |
| Signature Conversion | — | — | | Planejado: não há métrica própria; deriva-se de criados e concluídos |

Além disso, `orchestrator.security.denied` (contador, `reason` = `unauthenticated` ou `forbidden`). Não há métricas de profundidade de fila, atraso do outbox, tempo de execução por operação, runtime do .NET nem saúde do Worker.

> **Estado:** Implementado parcialmente.
>
> `TelemetryTests.Minimum_metrics_exist_and_move_with_a_full_flow`. Spec 008. D-065.

---

# 41. Alertas

Alertas deverão existir para:

```text
DLQ crescendo

Provider indisponível

Callback error rate elevado

Circuit breaker aberto

Artifact upload failures

Reconciliation discrepancies

Database saturation

Queue lag elevado

SLA de assinatura violado
```

> **Estado:** Planejado (Fase 2).
>
> Não há regras de alerta, Alertmanager nem dashboards. Parte dos dados já existe e serve de base:
>
> | Alerta | Dado disponível hoje |
> |---|---|
> | DLQ crescendo | `orchestrator.deadletter.size` |
> | Provider indisponível | `orchestrator.provider.errors` e `provider.latency` |
> | Callback error rate elevado | `orchestrator.callback.deliveries` |
> | Circuit breaker aberto | não aplicável (não há circuit breaker) |
> | Artifact upload failures | só pelo contador de retries e pelos eventos `OPERATION_*` |
> | Reconciliation discrepancies | `orchestrator.reconciliation.runs` com `outcome=CORRECTED` |
> | Database saturation | nenhum |
> | Queue lag elevado | nenhum |
> | SLA de assinatura violado | indicador `sla` do processo (`ALERT`); sem métrica |
>
> O SLA é visível no portal, mas ninguém é avisado. Spec 008 (fora de escopo); seção 47. D-066.

---

# 42. Requisitos Não Funcionais

## Disponibilidade

Target inicial:

```text
99,9%
```

A arquitetura deverá permitir evolução para patamares superiores.

> **Estado:** Planejado. A meta depende de infraestrutura de produção (réplicas, alta disponibilidade de banco, broker e storage) fora do repositório e **não foi medida**. O `docker-compose.yml` é de desenvolvimento: instância única, sem healthchecks para API, Worker e portal. Constitution (99,9% só em produção).

## Escalabilidade

API e workers deverão ser stateless quando possível.

Scaling deverá ocorrer horizontalmente.

> **Estado:** Implementado parcialmente. API e Worker não guardam estado local relevante; o outbox é seguro com várias instâncias (`SKIP LOCKED`) e os consumidores deduplicam pelo inbox. Dois controles são **por instância**, não distribuídos: o limite de criação por cliente e o limite de callbacks por host. Não há teste de escala horizontal.

## Processamento

Deverá ser possível escalar independentemente:

```text
API

Provider Workers

Callback Workers

Artifact Workers

Identity Workers

Reconciliation Workers
```

> **Estado:** Implementado parcialmente. Cada instância do Worker escolhe as filas que consome (`RabbitMq:Queues`) e se roda o reconciliador (`Reconciliation:Enabled`), o que permite separar provider, artifact, callback, identity e reconciliação em instâncias distintas. Por padrão, uma instância faz tudo. Não há isolamento por fornecedor (seção 20).

## Consistência

O sistema deverá operar preferencialmente utilizando:

```text
At-Least-Once Delivery
+
Idempotency
```

Não deverá depender de garantia distribuída de exactly-once.

> **Estado:** Implementado. Outbox, inbox e idempotência das seções 21 a 23. Efeitos externos (provider, storage, callback) são at-least-once e protegidos por idempotência do adapter.

## Durabilidade

Nenhuma operação confirmada pela API poderá ser perdida.

> **Estado:** Implementado. A API só responde 202 depois do commit que grava processo, operação e outbox; queda do broker não perde mensagens (`WorkflowTests.Broker_outage_loses_nothing_and_process_completes_after_recovery`). A durabilidade dos dados em si depende de backup e replicação do PostgreSQL e do storage, fora do repositório, sem teste de restauração.

> Specs 001, 003, 008. D-006, D-011, D-066.

---

# 43. APIs Principais

```text
POST /v1/signature-processes

GET /v1/signature-processes/{id}

POST /v1/signature-processes/{id}/cancel

GET /v1/signature-processes/{id}/status

GET /v1/signature-processes/{id}/operations

GET /v1/signature-processes/{id}/events

GET /v1/signature-processes/{id}/artifacts

POST /v1/signature-processes/{id}/download-link

POST /v1/signature-processes/{id}/retry

POST /v1/signature-processes/{id}/reconcile
```

Para Identity Proofing:

```text
POST /v1/proofing-sessions

GET /v1/proofing-sessions/{id}

POST /v1/proofing-sessions/{id}/documents

POST /v1/proofing-sessions/{id}/biometrics

GET /v1/proofing-sessions/{id}/result
```

## Contrato real

A API completa é documentada em `/swagger` (OpenAPI gerado). Convenções: JSON em `camelCase`; enums como texto; datas ISO-8601 em UTC; erros em `application/problem+json`; listas paginadas com `page` (1 por padrão), `pageSize` (50 por padrão, máximo 200) e o envelope `{ "items": [...], "page": 1, "pageSize": 50, "total": 0 }`.

Papéis: V = viewer, O = operator, C = client, A = admin (matriz na seção 37).

### Processos de assinatura

| Método e rota | Papéis | Sucesso | Observações |
|---|---|---|---|
| `POST /v1/signature-processes` | O, C, A | 202 (200 no replay) | `Idempotency-Key` obrigatório; seção 6. |
| `GET /v1/signature-processes` | V, O, C, A | 200 | Filtros `status`, `operationalStatus`, `q`; cada item traz `progress` resumido. Cliente vê só os seus. |
| `GET /v1/signature-processes/{id}` | V, O, C, A | 200 | Signatários mascarados, confirmações e `progress` com `steps`. |
| `GET .../status` | V, O, C, A | 200 | `{ processId, businessStatus, operationalStatus, updatedAt }`. |
| `GET .../operations` | V, O, C, A | 200 | Paginada. |
| `GET .../events` | V, O, C, A | 200 | Journal paginado. |
| `GET .../artifacts` | V, O, C, A | 200 | Sem paginação. |
| `POST .../download-link` | O, C, A | 200 | Seção 31. |
| `GET .../callbacks` | V, O, C, A | 200 | Entregas de callback. |
| `GET .../provider` | O, C, A | 200 | Seção 28; auditado. |
| `POST .../cancel` | O, C, A | 200 | Idempotente se já cancelado; outro estado terminal responde 409. |
| `POST .../retry` | O, C, A | 200 | Seção 14. |
| `POST .../reconcile` | O, C, A | 200 | Seção 18; terminal responde 409. |
| `GET .../reconciliations` | V, O, C, A | 200 | Histórico. |
| `POST .../signers/{signerId}/confirmations/{channel}/confirm` | O, C, A | 200 | Corpo `{ "code": "123456" }`; seção 6.3. |
| `POST .../signers/{signerId}/confirmations/{channel}/resend` | O, C, A | 202 | Seção 6.3. |

### Documentos, downloads e callbacks

| Método e rota | Papéis | Sucesso | Observações |
|---|---|---|---|
| `POST /v1/document-uploads` | O, C, A | 201 | Multipart, campo `file`; seção 6.4. |
| `GET /v1/downloads/{artifactId}?expires&sig` | pública | 200 | Seção 31. |
| `POST /v1/callbacks` | C, A | 201 | Seção 8. |
| `GET /v1/callbacks`, `GET /v1/callbacks/{callbackId}` | V, O, C, A | 200 | Sem segredo. |
| `DELETE /v1/callbacks/{callbackId}` | C, A | 204 | Desativa. |
| `GET /v1/dead-letters` | V, O, C, A | 200 | Seção 17. |

### Identity Proofing

| Método e rota | Papéis | Sucesso |
|---|---|---|
| `POST /v1/proofing-sessions` | C, A | 202 (200 no replay) |
| `GET /v1/proofing-sessions/{id}`, `.../result`, `.../operations`, `.../events` | V, O, C, A | 200 |
| `POST .../documents`, `POST .../biometrics` | C, A | 202 |
| `POST .../retry`, `DELETE .../evidence` | O, C, A | 200, 204 |

### Infraestrutura e suporte

| Método e rota | Acesso | Observações |
|---|---|---|
| `POST /v1/webhooks/{provider}` | pública, assinada pelo provider | `docusign` e `lacuna`; fora do Swagger; seção 18. |
| `GET /v1/dev/confirmation-codes/{processId}` | A | Só com `Confirmation:ExposeSink=true`; fora do Swagger; só desenvolvimento. |
| `GET /health` | pública | `Healthy` ou 503 `Unhealthy` (verifica o PostgreSQL). |
| `GET /swagger` | pública | Interface e `swagger.json`. |

### Catálogo de erros

Todo erro de domínio é `application/problem+json` com `type`, `title`, `status`, `detail`, `correlationId` e, na validação, `errors`.

| HTTP | `title` | Quando |
|---|---|---|
| 400 | `Validation failed` | Corpo ou campo inválido; `Idempotency-Key` ausente ou acima de 128; filtro desconhecido; `X-Operator-Id` inválido. `errors` traz a chave do campo (por exemplo `signers[0].order`). |
| 401 | `Authentication required` | Sem token válido (com `WWW-Authenticate: Bearer`). |
| 401 | `Invalid webhook signature` | Webhook com assinatura ausente ou inválida (corpo simples `{ title, status }`). |
| 403 | `Forbidden` | Papel sem permissão para a rota. |
| 403 | `Invalid download link` | `sig` ausente ou inválida. |
| 404 | `Not found` | Recurso inexistente **ou de outro cliente**. |
| 409 | `Idempotency conflict` | Mesma chave com corpo diferente. |
| 409 | `Invalid state transition` | Cancelar processo em outro estado terminal. |
| 409 | `Conflict` | Estado incompatível: reprocessar, reconciliar, confirmar ou reenviar código, callback ou evidência duplicados. |
| 409 | `Concurrent modification, retry the request` | Conflito de concorrência não absorvido. |
| 410 | `Code expired` | Código de confirmação vencido. |
| 410 | `Download link expired` | Link vencido. |
| 413 | (sem corpo) | Webhook acima de 1 MiB. |
| 422 | `Code does not match` | Código errado; traz `attemptsRemaining`. |
| 423 | `Code locked` | Cinco códigos errados. |
| 429 | `Too many requests` | Reenvio de código (com `Retry-After` quando é o intervalo); limite de criação responde 429 sem corpo. |
| 500 | `Internal error` | Falha não mapeada; `detail` nulo. |

> **Estado:** Implementado.
>
> Riscos declarados: o tamanho máximo de corpo e de upload depende dos padrões do framework (e do nginx, 20 MB, no caminho do portal); erros de binding do framework fora do mapeamento podem não sair como `problem+json` (seção 54). Specs 001 a 011. D-003, D-047, D-080.

---

# 44. Modelo Conceitual

Entidades principais:

```text
SignatureProcess

Signer

Document

Artifact

IdentityProofingSession

IdentityValidation

ProviderProcess

Operation

Event

Callback

Retry

Audit

Policy
```

Relação conceitual:

```text
SignatureProcess
      │
      ├── Document
      │      └── Artifact
      │
      ├── Signer
      │      └── IdentityProofingSession
      │
      ├── ProviderProcess
      │
      ├── Operation
      │
      ├── Event
      │
      └── Callback
```

## Como foi implementado

| Entidade do PRD | Na solução | Observação |
|---|---|---|
| SignatureProcess | `signature_process` | Com `version` (concorrência), `client_id` (dono) e `last_reconciled_at`. |
| Signer | `signer` e `signer_confirmation` | Tipo, ordem, canais, liberação e, por canal, o estado da confirmação. |
| Document | Sem entidade própria | Nome do arquivo no processo, conteúdo como artefato `ORIGINAL_DOCUMENT`; documentos pré-processo em `document_upload`. |
| Artifact | `artifact` | Um por tipo e por processo ou sessão. |
| IdentityProofingSession | `proofing_session` | **Independente** do processo e do signatário: não há vínculo entre elas. |
| IdentityValidation | `identity_validation` | Uma por capability e sessão. |
| ProviderProcess | `provider_process` | Fixa o provider do processo. |
| Operation | `operation` | Pertence a um processo **ou** a uma sessão de proofing. |
| Event | `journal_event` | Append-only. |
| Callback | `callback_registration` e `callback_delivery` | Destinos e entregas. |
| Retry | Atributos de `operation` e `dead_letter_entry` | Sem entidade própria. |
| Audit | `journal_event` | Mesma tabela do evento, com ator. |
| Policy | — | Não existe (seção 10). |

Entidades de suporte acrescentadas: `idempotency_record`, `outbox_event`, `inbox_message`, `reconciliation_record`, `notification_sink` (só desenvolvimento).

Relação real:

```text
SignatureProcess
      │
      ├── Signer ── SignerConfirmation (por canal)
      │
      ├── Artifact (original, assinado, evidência)
      │
      ├── ProviderProcess
      │
      ├── Operation ── DeadLetterEntry
      │      └── CallbackDelivery
      │
      ├── JournalEvent
      │
      └── ReconciliationRecord

ProofingSession (independente)
      ├── IdentityValidation
      ├── Artifact (documento, selfie)
      └── Operation, JournalEvent
```

Colunas, índices e migrações em [ARCHITECTURE.md](ARCHITECTURE.md).

> **Estado:** Implementado.
>
> Specs 001 a 011. D-044.

---

# 45. Fora do Escopo Inicial

Não fazem parte necessariamente do MVP:

- editor avançado de documentos;
- geração de contratos;
- criação dinâmica de documentos;
- workflow BPM genérico;
- gestão jurídica de contratos;
- assinatura manuscrita física;
- KYC/AML completo;
- PEP;
- sanctions;
- credit scoring.

Essas funcionalidades podem consumir a plataforma posteriormente.

> **Estado:** Inalterado. Nenhum dos itens acima foi implementado, e continuam fora do escopo. Também permanecem fora, por decisão, a rotação de segredos (D-037), o logout federado do Keycloak e a limpeza de uploads não consumidos.

---

# 46. MVP

O MVP deverá possuir:

```text
Signature API

1 provider de assinatura

1 provider de Identity Proofing

PostgreSQL + JSONB

Object Storage

Queue / Event Bus

State Machine

Event Journal

Callback

Retry

DLQ

Reconciliation básico

Operations Portal

Documento original

Documento final

Download seguro

Auditoria
```

## Situação do MVP

O MVP está **entregue** (specs 001 a 008).

| Item | Estado | Observação |
|---|---|---|
| Signature API | Implementado | Seção 43. |
| 1 provider de assinatura | Implementado | Provider simulado; DocuSign e Lacuna entraram depois (spec 011). |
| 1 provider de Identity Proofing | Implementado parcialmente | Adapter simulado. |
| PostgreSQL + JSONB | Implementado | Seção 25. |
| Object Storage | Implementado | MinIO por API S3. |
| Queue / Event Bus | Implementado | RabbitMQ. |
| State Machine | Implementado parcialmente | `FAILED` e `EXPIRED` sem gatilho. |
| Event Journal | Implementado | Append-only por trigger. |
| Callback | Implementado | Assinado, com registro de destinos. |
| Retry | Implementado | Seção 15. |
| DLQ | Implementado | Seção 17. |
| Reconciliation básico | Implementado | Seção 18. |
| Operations Portal | Implementado | Seções 34 a 36. |
| Documento original | Implementado | |
| Documento final | Implementado | |
| Download seguro | Implementado parcialmente | Sem revogação e sem uso único. |
| Auditoria | Implementado | |

**Entregue além do MVP original**: signatários com tipo, ordem e canais; código de confirmação; progresso em etapas; upload de documento; criação de processo no portal; segregação por cliente ampliada; login com PKCE e renovação de token; adapters de DocuSign e Lacuna com webhooks; limites e rate limit; observabilidade com OpenTelemetry.

---

# 47. Fase 2

Adicionar:

```text
Múltiplos providers

Provider routing

Fallback automático

Identity Proofing Policies

Circuit Breaker

Bulkhead

Reprocessing avançado

Dashboard operacional

SLA management

Alertas

Métricas avançadas
```

## Situação da Fase 2

| Item | Estado | O que existe e o que falta |
|---|---|---|
| Múltiplos providers | Implementado parcialmente | Simulado, DocuSign e Lacuna atrás da mesma interface; seleção por configuração global. Falta validar os reais e acrescentar outros fornecedores. |
| Provider routing | Planejado | O router só encaminha ao provider já gravado no processo; não há escolha por regra. |
| Fallback automático | Planejado | |
| Identity Proofing Policies | Planejado | Seção 10. |
| Circuit Breaker | Planejado | Seção 19. |
| Bulkhead | Implementado parcialmente | Isolamento por domínio; falta por fornecedor (seção 20). |
| Reprocessing avançado | Planejado | Reprocessar a partir de operação concluída, invalidando as seguintes. |
| Dashboard operacional | Planejado | O portal tem lista e detalhe; não há painel agregado nem dashboards de métricas. |
| SLA management | Implementado parcialmente | Indicador `OK`/`ALERT` simples (60 minutos); sem SLA por tipo, sem gestão nem notificação. |
| Alertas | Planejado | Seção 41. |
| Métricas avançadas | Implementado parcialmente | Doze métricas mínimas; faltam fila, atraso e execução por operação. |

---

# 48. Fase 3

Adicionar capacidades de plataforma:

```text
Dynamic Provider Selection

Provider Cost Optimization

Risk-Based Identity Proofing

Policy Engine

Multi-region

Advanced Fraud Detection

Adaptive Authentication

Advanced SLA Routing
```

> **Estado:** Planejado. Nenhum item da Fase 3 existe no código.

---

# 49. Critérios de Aceite do MVP

Um sistema consumidor deverá conseguir:

1. Enviar um documento.
2. Criar um processo de assinatura.
3. Informar signatários.
4. Definir as validações de identidade.
5. Definir callback.
6. Consultar o estado.
7. Receber atualizações por callback.
8. Acompanhar todas as operações.
9. Recuperar documento original.
10. Recuperar documento assinado.
11. Consultar evidências.
12. Reprocessar uma operação.
13. Recuperar automaticamente de falhas transitórias.
14. Identificar operações em DLQ.
15. Visualizar o processo através do portal operacional.
16. Baixar documento através de URL temporária segura.

## Cobertura por teste automatizado

Os 16 critérios têm teste automatizado, com duas ressalvas nos itens 4 e 11. Os nomes são `Classe.método` dos projetos `Orchestrator.UnitTests` e `Orchestrator.IntegrationTests` (cada um pode existir em ambas as suítes; os de integração usam Postgres, RabbitMQ e MinIO reais) e arquivos de `portal/src/__tests__`. O `scripts/smoke-test.sh`, executado contra o `docker compose`, cobre os mesmos fluxos de ponta a ponta e não é contado como teste automatizado de suíte.

| # | Critério | Estado | Teste automatizado |
|---|---|---|---|
| 1 | Enviar um documento | Implementado | `SignerConfirmationTests.Uploaded_document_is_the_source_of_a_process`; `HumanCreationTests.Document_upload_role_matrix`; `ArtifactWorkflowTests.Document_over_the_size_limit_is_rejected`; por URL: `WorkflowTests.Process_reaches_completed_with_all_operations_and_journal_events` |
| 2 | Criar um processo de assinatura | Implementado | `CreateProcessTests.First_creation_returns_202_and_replay_returns_200_with_same_process`; `CreateProcessValidationTests.Valid_payload_has_no_errors` |
| 3 | Informar signatários | Implementado | `SignerValidationTests.Simple_case_is_names_and_contacts_with_defaults`; `SignerConfirmationTests.Defaults_are_inherited_and_contacts_are_masked`; `SignerConfirmationTests.Invalid_signers_are_reported_with_signer_and_field` |
| 4 | Definir as validações de identidade | Implementado parcialmente | `CreateProcessValidationTests.Unknown_capability_is_rejected`; `IdentityCapabilitiesTests.Catalog_contains_the_eleven_prd_capabilities`; `ProofingSessionTests.Session_with_only_person_data_completes_without_any_signature_process`. **Ressalva:** as validações pedidas no processo são registradas e devolvidas, mas não executadas; a execução é testada na sessão de proofing, não ligada ao processo (seção 6) |
| 5 | Definir callback | Implementado | `CallbackRegistrationTests.Registration_returns_the_secret_once_and_never_again`; `CallbackDeliveryTests.Registered_callback_id_uses_the_registered_url_and_secret`; `CreateProcessValidationTests.Callback_with_both_url_and_callback_id_is_rejected` |
| 6 | Consultar o estado | Implementado | `QueryTests.Get_process_returns_masked_signers_and_states`; `PortalApiTests.Detail_includes_document_provider_and_sla`; `RetryTests.Retry_pending_state_exposes_attempt_max_attempts_and_next_retry_time_and_cancel_stops_it` |
| 7 | Receber atualizações por callback | Implementado | `CallbackDeliveryTests.Dynamic_url_receives_signed_events_through_to_completion`; `CallbackDeliveryTests.Provider_rejection_and_cancellation_are_notified`; `CallbackResilienceTests.Receiver_failures_are_retried_with_stable_event_ids_and_never_touch_the_process_state` |
| 8 | Acompanhar todas as operações | Implementado | `QueryTests.Operations_and_events_are_paginated`; `WorkflowTests.Process_reaches_completed_with_all_operations_and_journal_events`; portal `ProcessDetail.test.tsx` ("Operations tab shows type, status, attempts, next retry and error") |
| 9 | Recuperar documento original | Implementado | `DownloadTests.Original_can_be_downloaded_by_type_and_equals_the_source_document` |
| 10 | Recuperar documento assinado | Implementado | `DownloadTests.Default_link_is_the_signed_document_and_content_matches_the_listed_hash`; `ArtifactServiceTests.Provider_signed_document_differs_from_original_and_evidence_is_deterministic` |
| 11 | Consultar evidências | Implementado | `ArtifactWorkflowTests.Completed_process_has_original_signed_and_evidence_with_correct_hashes`; `ProviderTests.DocuSign_end_to_end_creates_one_envelope_signs_in_order_and_stores_signed_document_and_evidence`; `ProofingRetentionTests.Evidence_cannot_be_downloaded` (evidência de proofing). **Ressalva:** o teste cobre a listagem e o hash; não há teste que baixe o conteúdo da evidência do processo |
| 12 | Reprocessar uma operação | Implementado | `ReprocessTests.Retry_resumes_from_the_failed_operation_without_repeating_completed_ones`; `ReprocessTests.Retry_after_a_permanent_failure_with_explicit_operation_id`; `ReprocessTests.Concurrent_retries_are_accepted_once` |
| 13 | Recuperar automaticamente de falhas transitórias | Implementado | `RetryTests.Transient_provider_failures_are_retried_until_the_process_completes`; `RetryTests.Source_503_is_transient_and_recovers_when_the_source_recovers`; `WorkflowTests.Broker_outage_loses_nothing_and_process_completes_after_recovery` |
| 14 | Identificar operações em DLQ | Implementado | `DeadLetterTests.Exhausted_provider_operation_goes_to_the_provider_dlq_with_references_only`; `DeadLetterTests.Dead_letter_listing_validates_domain_and_paginates`; portal `DeadLetters.test.tsx` |
| 15 | Visualizar o processo no portal operacional | Implementado | Portal: `ProcessList.test.tsx`, `ProcessDetail.test.tsx`, `ManualOperations.test.tsx`, `DeadLetters.test.tsx`, `NewProcess.test.tsx`, `Progress.test.tsx`; `PortalApiTests.List_returns_the_columns_of_the_portal`. **Ressalva:** o portal é testado em jsdom com API simulada; não há teste em navegador real |
| 16 | Baixar documento por URL temporária segura | Implementado | `DownloadLinkSignerTests.Valid_link_verifies_until_expiry`; `DownloadLinkSignerTests.Tampered_signature_expiry_or_artifact_is_invalid`; `DownloadTests.Tampered_signature_and_expiry_are_forbidden`; `DownloadTests.Expired_link_is_gone_and_leaves_no_download_event`; `DownloadTests.Link_is_scoped_to_a_single_artifact` |

## 49.1 Critérios acrescentados na versão 2.0

Não alteram os 16 anteriores. Um sistema consumidor ou operador deverá conseguir:

| # | Critério | Estado | Teste automatizado |
|---|---|---|---|
| 17 | Informar tipo de assinatura, ordem e canais de confirmação por signatário, com `defaults` | Implementado | `SignerValidationTests.Signer_values_override_defaults_and_empty_list_disables_confirmation`; `SignerValidationTests.Order_without_gaps_is_accepted`; `SignerConfirmationTests.Order_without_confirmation_signs_in_sequence_and_equal_orders_sign_together` |
| 18 | Exigir confirmação por código antes da assinatura, com validade, tentativas e reenvio | Implementado (com notificador simulado) | `SignerConfirmationTests.Nobody_signs_before_every_channel_is_confirmed_and_then_the_process_completes`; `SignerConfirmationTests.Five_wrong_codes_lock_and_a_resend_issues_a_new_one`; `SignerConfirmationTests.Expired_code_answers_410_and_a_resend_recovers`; `SignerConfirmationTests.The_code_never_reaches_events_operations_responses_or_logs` |
| 19 | Acompanhar o progresso em etapas X/Y | Implementado | `ProgressCalculatorTests.Plan_counts_document_confirmations_signatures_final_and_callback`; `SignerConfirmationTests.Progress_reaches_the_total_only_after_the_final_callback_is_delivered`; portal `Progress.test.tsx` |
| 20 | Criar processo pelo portal como `operator` ou `admin` | Implementado | `HumanCreationTests.An_operator_creates_a_process_through_the_portal_path_and_is_recorded_as_the_actor`; `HumanCreationTests.Process_creation_role_matrix`; portal `CreateAccess.test.tsx` |
| 21 | Ver apenas os recursos do próprio cliente | Implementado | `SegregationTests.Every_resource_endpoint_answers_404_to_a_client_that_does_not_own_the_resource`; `AuthApiTests.Clients_only_see_and_operate_their_own_processes` |
| 22 | Entrar no portal com PKCE e manter a sessão por renovação do token | Implementado | portal `Auth.test.tsx` ("sign-in with authorization code and PKCE"); portal `TokenRefresh.test.tsx` |
| 23 | Escolher o provider por configuração, mantendo cada processo no provider em que nasceu | Implementado (contra servidores falsos) | `ProviderTests.A_process_stays_with_the_provider_it_started_with_when_the_default_changes`; `ProviderTests.DocuSign_end_to_end_creates_one_envelope_signs_in_order_and_stores_signed_document_and_evidence`; `ProviderTests.Lacuna_end_to_end_creates_one_document_with_the_flow_and_stores_signed_document_and_evidence` |

---

# 50. Definition of Done

O produto estará apto para produção quando:

```text
Todos os fluxos críticos estiverem idempotentes

Retry estiver implementado

DLQ estiver implementada

Reconciliation estiver funcional

Documentos estiverem preservados internamente

Audit Trail estiver completo

Callbacks estiverem assinados

Artefatos tiverem hash

URLs de download forem temporárias

Identity Proofing estiver desacoplado de fornecedor

Provider Adapter estiver desacoplado do domínio

Observabilidade estiver implementada

Runbooks operacionais estiverem disponíveis

Testes de carga estiverem concluídos

Testes de caos dos principais providers estiverem concluídos

Testes de segurança estiverem concluídos
```

## Situação

**O produto ainda não está apto para produção segundo esta definição**: os quatro últimos itens (runbooks, carga, caos e segurança) não foram atendidos, e há ressalvas em outros quatro. Os fluxos funcionais do MVP estão entregues e verificados.

| Item | Estado | Teste automatizado |
|---|---|---|
| Fluxos críticos idempotentes | Atendido | `CreateProcessTests.Fifty_concurrent_requests_with_the_same_key_create_exactly_one_process`; `WorkflowTests.Duplicate_message_delivery_has_no_side_effects`; `ReprocessTests.Concurrent_retries_are_accepted_once`; `ReconciliationServiceTests.Reconciliation_is_idempotent`; `ArtifactWorkflowTests.Redelivered_download_operation_does_not_create_a_second_artifact` |
| Retry implementado | Atendido | `RetryPolicyTests.Default_schedule_follows_the_PRD`; `RetryTests.Transient_provider_failures_are_retried_until_the_process_completes` |
| DLQ implementada | Atendido | `DeadLetterTests.Exhausted_provider_operation_goes_to_the_provider_dlq_with_references_only`; `CallbackResilienceTests.Exhausted_deliveries_go_to_the_callback_dlq_and_can_be_resent_manually_after_the_process_is_terminal` |
| Reconciliation funcional | Atendido (providers reais só contra servidores falsos) | `ReconciliationTests.Divergent_process_is_corrected_and_the_workflow_continues_to_completion_with_callbacks`; `ReconciliationApiTests.Manual_reconciliation_corrects_a_divergent_process_and_records_it`; `ProviderTests.Scheduled_or_manual_reconciliation_corrects_a_DocuSign_process_without_any_webhook` |
| Documentos preservados internamente | Parcial | `ArtifactWorkflowTests.Completed_process_has_original_signed_and_evidence_with_correct_hashes`; `ArtifactServiceTests.RequireStored_fails_when_object_is_missing_from_store`. Faltam teste de durabilidade e de restauração do storage e política de retenção |
| Audit Trail completo | Parcial | `JournalTests.Journal_is_append_only`; `AuthApiTests.Operator_operates_and_the_journal_records_the_token_identity_not_the_header`; `PortalApiTests.Provider_metadata_are_returned_and_the_access_is_audited_with_the_operator`. Falta `SIGNER_OPENED_DOCUMENT`, evento de falha de callback e retenção |
| Callbacks assinados | Atendido | `CallbackSignerTests.Known_vector_matches_hmac_sha256_of_timestamp_dot_body`; `CallbackSignerTests.Replay_outside_the_tolerance_is_rejected_in_both_directions`; `CallbackDeliveryTests.Signature_made_with_a_different_secret_is_invalid_for_the_receiver` |
| Artefatos com hash | Atendido | `ArtifactServiceTests.Stores_object_and_records_sha256_size_and_content_type`; `ArtifactWorkflowTests.Completed_process_has_original_signed_and_evidence_with_correct_hashes` |
| URLs de download temporárias | Atendido | `DownloadLinkSignerTests.Expired_link_is_reported_expired`; `DownloadTests.Expired_link_is_gone_and_leaves_no_download_event` |
| Identity Proofing desacoplado de fornecedor | Parcial | `FakeIdentityAdapterTests.Adapter_contract_exposes_no_vendor_types`; `ProofingSessionTests.Invalid_cpf_and_vendor_fields_are_rejected`. Só existe o adapter simulado: a troca por um fornecedor real não foi provada |
| Provider Adapter desacoplado do domínio | Atendido | `FakeProviderAdapterTests.Adapter_contract_exposes_no_vendor_types`; `ProviderTests.A_process_stays_with_the_provider_it_started_with_when_the_default_changes` |
| Observabilidade implementada | Parcial | `TelemetryTests.One_trace_connects_the_request_and_the_worker_operations_with_correlation_tags`; `TelemetryTests.Minimum_metrics_exist_and_move_with_a_full_flow`. Sem alertas, dashboards nem backend de traces e logs |
| Runbooks operacionais | **Não atendido** | Sem teste automatizado (não há runbooks) |
| Testes de carga | **Não atendido** | Sem teste automatizado. Existem só testes de concorrência de corretude (por exemplo `CreateProcessTests.Fifty_concurrent_requests_with_the_same_key_create_exactly_one_process`) |
| Testes de caos dos principais providers | **Não atendido** | Sem suíte de caos. Cobertura próxima: `ProviderTests.DocuSign_transient_failures_are_retried_without_duplicating_the_envelope_and_permanent_ones_stop_the_operation`, `ProviderTests.Lacuna_transient_failure_is_retried_and_cancel_reaches_the_provider`, `WorkflowTests.Broker_outage_loses_nothing_and_process_completes_after_recovery` |
| Testes de segurança | **Não atendido** (cobertura funcional parcial) | Sem teste de penetração, DAST ou varredura de dependências. Testes funcionais de segurança: `SsrfGuardTests`, `GuardedConnectionTests`, `AuthApiTests`, `SegregationTests`, `ProviderTests.Credentials_and_tokens_never_reach_logs_events_or_responses`, `ProviderTests.Webhook_with_a_bad_or_missing_signature_is_refused_and_changes_nothing` |

**Última verificação.** Reexecutados nesta revisão (06/10/2026): 278 testes unitários passando; 240 de integração passando e 2 pulados (testes contra sandboxes reais, sem credenciais); 102 testes do portal passando. O `scripts/smoke-test.sh` contra o `docker compose` consta como aprovado em `docs/PROGRESS.md` e **não** foi reexecutado nesta revisão.

---

# 51. Visão Arquitetural Final

```text
                         ┌────────────────────────┐
                         │ Sistemas Consumidores  │
                         └───────────┬────────────┘
                                     │
                                  HTTPS
                                     │
                              ┌──────▼──────┐
                              │ API Gateway │
                              └──────┬──────┘
                                     │
                              ┌──────▼──────┐
                              │Signature API│
                              └──────┬──────┘
                                     │
                        Transaction + Outbox
                                     │
                    ┌────────────────┼────────────────┐
                    │                │                │
                    ▼                ▼                ▼
             PostgreSQL          Object Store     Event Bus
               + JSONB                              │
                                                   │
                  ┌────────────────────────────────┼───────────────────────┐
                  │                                │                       │
                  ▼                                ▼                       ▼
           Provider Workers                Identity Workers       Callback Workers
                  │                                │                       │
           Provider Router                  Proofing Router          Consumer APIs
                  │                                │
      ┌───────────┼──────────┐          ┌─────────┼──────────┐
      │           │          │          │         │          │
      ▼           ▼          ▼          ▼         ▼          ▼
  DocuSign    Clicksign    Adobe      Unico    Serasa    Datavalid


                         ┌───────────────────────┐
                         │ Reconciliation Worker │
                         └───────────┬───────────┘
                                     │
                              Providers / State


                         ┌───────────────────────┐
                         │   Operations Portal   │
                         └───────────┬───────────┘
                                     │
                             Operations API
```

## Correspondência com a solução

| Componente da visão | Estado | Na solução |
|---|---|---|
| Sistemas Consumidores | Externo | Clientes de API (client credentials) e pessoas pelo portal. |
| API Gateway | Fora da solução | Infraestrutura externa (TLS, WAF, egress). |
| Signature API | Implementado | `Orchestrator.Api`, porta 8080 no compose. |
| PostgreSQL + JSONB | Implementado | PostgreSQL 16. |
| Object Store | Implementado | MinIO por API S3. |
| Event Bus | Implementado | RabbitMQ, uma fila e uma DLQ por domínio. |
| Provider, Identity e Callback Workers | Implementado | Consumidores por fila no `Orchestrator.Worker` (um processo). |
| Provider Router | Implementado parcialmente | `ProviderRouter`: simulado, DocuSign e Lacuna. Clicksign e Adobe: planejado. |
| Proofing Router | Planejado | Existe um único adapter de identidade (simulado); Unico, Serasa e Datavalid: planejado. |
| Reconciliation Worker | Implementado | Serviço de segundo plano no Worker. |
| Operations Portal / Operations API | Implementado | `portal/` (React e nginx, porta 3000) sobre a mesma API `/v1`. |

Detalhes técnicos, portas e configuração: [ARCHITECTURE.md](ARCHITECTURE.md).

---

# 52. Definição do Produto

A Plataforma de Orquestração de Assinaturas deverá ser responsável pelo ciclo de vida integral de processos de assinatura, desde o recebimento e preservação do documento original, passando pela comprovação de identidade, execução da assinatura em provedores externos, controle do workflow, resiliência, reprocessamento e reconciliação, até a preservação das evidências e disponibilização segura do documento final.

A plataforma deverá tratar **Identity Proofing e Signature Orchestration como bounded contexts distintos**, permitindo que Identity Proofing seja utilizado por outros domínios corporativos independentemente da existência de um processo de assinatura.

O princípio central da solução será:

> **Os sistemas consumidores determinam as capacidades e evidências necessárias. A plataforma decide como executá-las, com qual fornecedor, como preservar as evidências e como garantir a conclusão resiliente do processo.**

> **Estado:** Implementado parcialmente. Os dois contextos já são distintos no código e na API (sessões de proofing não dependem de processo de assinatura). O que ainda não existe é a ligação opcional entre eles (uma assinatura que exija uma sessão aprovada) e a decisão da plataforma sobre qual fornecedor executar cada capacidade (Fases 2 e 3).

---

# 53. Rastreabilidade

Seção acrescentada na versão 2.0. Liga cada seção do PRD às specs em `specs/` e às decisões `D-xxx` de [DECISIONS.md](DECISIONS.md). As decisões não são citadas dentro das specs; a ligação vem da coluna *Spec* de DECISIONS.md. Seções sem requisito (2, 3, 45, 51 e 52, que são contexto, escopo ou visão) aparecem só quando há algo a rastrear.

## 53.1 Seção do PRD → specs e decisões

| Seção | Tema | Estado | Specs | Decisões |
|---|---|---|---|---|
| 1 | Visão do produto | Implementado parcialmente | 001, 002, 005, 011 | D-002 |
| 3 | Objetivos | Implementado parcialmente | 001 a 011 | — |
| 4 | Princípios de arquitetura | Implementado parcialmente | 001 a 011, constituição | D-006, D-010 |
| 5 | Escopo funcional | Implementado parcialmente | 001 a 011 | — |
| 6 | Criação de processo | Implementado parcialmente | 001, 002, 010, 011 | D-003, D-004, D-072 a D-075, D-079, D-080, D-085, D-089, D-090 |
| 7 | Callback | Implementado | 004, 001 | D-033, D-034, D-036, D-038 a D-040 |
| 8 | Segurança de callback | Implementado parcialmente | 004, 009 | D-034, D-035, D-038, D-039, D-067, D-068 |
| 9 | Identity Proofing | Implementado parcialmente | 005, 009 | D-041 a D-048, D-067 |
| 10 | Políticas de Identity Proofing | Planejado | 005 (fora de escopo) | — |
| 11 | Máquina de estados | Implementado parcialmente | 001, 006 | D-007, D-012, D-014 |
| 12 | Estado operacional | Implementado parcialmente | 001, 003, 010 | D-011, D-012, D-033, D-076 |
| 13 | Operações | Implementado parcialmente | 001, 002, 003, 010 | D-011, D-013, D-026, D-044, D-076 |
| 14 | Reprocessamento | Implementado | 003, 004, 005 | D-025, D-036, D-040, D-047 |
| 15 | Retry | Implementado | 003 | D-024, D-028, D-030 a D-032 |
| 16 | Classificação de erros | Implementado | 003, 011 | D-012, D-022, D-028, D-087 |
| 17 | Dead Letter Queue | Implementado | 003, 010 | D-026, D-027, D-067, D-076 |
| 18 | Reconciliação | Implementado parcialmente | 006, 011 | D-049 a D-055, D-086 |
| 19 | Circuit Breaker | Planejado | — (constituição V) | — |
| 20 | Bulkhead | Implementado parcialmente | 003, 010 | D-026, D-076 |
| 21 | Idempotência | Implementado | 001, 003, 005, 011 | D-003, D-004, D-014, D-090 |
| 22 | Outbox Pattern | Implementado | 001, 003 | D-011, D-013 |
| 23 | Inbox Pattern | Implementado | 001 | D-011 |
| 24 | Event Journal | Implementado parcialmente | 001, 003, 004, 006, 007, 010 | D-021, D-057, D-059, D-090 |
| 25 | Armazenamento | Implementado | 001 a 011 | D-002, D-010, D-011, D-014 |
| 26 | Artifact Store | Implementado parcialmente | 002, 005, 010, 011 | D-017, D-020, D-045, D-080 |
| 27 | Integridade dos artefatos | Implementado | 002 | D-016, D-017 |
| 28 | Provider Metadata | Implementado parcialmente | 001, 007, 011 | D-059 |
| 29 | Provider Adapters | Implementado parcialmente | 001, 006, 011 | D-005, D-082 a D-088 |
| 30 | Artifact Delivery | Implementado parcialmente | 002 | D-018 |
| 31 | Download seguro | Implementado parcialmente | 002, 009 | D-016, D-045, D-061, D-067 |
| 32 | Arquitetura de processamento | Implementado | 001, 003 | D-006, D-010, D-011, D-026 |
| 33 | Reconciliation Worker | Implementado parcialmente | 006 | D-049, D-050, D-054, D-055 |
| 34 | Operations Portal | Implementado | 007, 010 | D-056, D-058, D-061, D-081, D-089, D-092 |
| 35 | Detalhes do processo | Implementado parcialmente | 007, 010 | D-078, D-081 |
| 36 | Operações manuais | Implementado | 007, 008 | D-057, D-059, D-062, D-090 |
| 37 | Segurança | Implementado parcialmente | 008, 009, 011 | D-062 a D-070, D-083, D-086, D-089 a D-092 |
| 38 | Proteção de dados | Implementado parcialmente | 005, 010 | D-045, D-048, D-074, D-079, D-080 |
| 39 | Observabilidade | Implementado parcialmente | 008 | D-065 |
| 40 | Métricas | Implementado parcialmente | 008 | D-065 |
| 41 | Alertas | Planejado | 008 (fora de escopo) | D-066 |
| 42 | Requisitos não funcionais | Implementado parcialmente | 001, 003, 008 | D-006, D-011, D-066 |
| 43 | APIs principais | Implementado | 001 a 011 | D-003, D-047, D-080 |
| 44 | Modelo conceitual | Implementado | 001 a 011 | D-044 |
| 46 | MVP | Implementado | 001 a 008 | — |
| 47 | Fase 2 | Implementado parcialmente | 003, 006, 007, 008, 011 | D-066, D-082 |
| 48 | Fase 3 | Planejado | — | — |
| 49 | Critérios de aceite | 16 de 16 com teste (ressalvas em 4 e 11) | 001 a 011 | — |
| 50 | Definition of Done | Parcial (4 itens não atendidos) | 001 a 011 | — |

## 53.2 Spec → seções do PRD

| Spec | Tema | Seções do PRD |
|---|---|---|
| 001 | Núcleo de orquestração | 6, 11, 12, 13, 21 a 25, 32, 43 |
| 002 | Artefatos e download seguro | 25 a 27, 30, 31 |
| 003 | Resiliência, retry, DLQ e reprocessamento | 14 a 17, 20, 22 |
| 004 | Callbacks assinados e seguros | 7, 8 |
| 005 | Identity Proofing por capabilities | 9, 10 (fora de escopo), 38, 43 |
| 006 | Reconciliação básica | 18, 33 |
| 007 | Portal operacional | 34, 35, 36 |
| 008 | Segurança (OIDC e RBAC) e observabilidade | 37, 39, 40, 41 (fora de escopo) |
| 009 | Endurecimento de segurança | 8, 31, 37 |
| 010 | Signatários, confirmação e progresso | 6, 12, 13, 34, 35, 38 |
| 011 | Providers reais em sandbox | 18, 29, 37 |

As specs 001 a 008 citam seções do PRD por número no próprio texto (por exemplo "PRD seção 18"); as specs 009, 010 e 011 não citam nenhuma, e sua relação com o PRD está registrada nesta tabela.

## 53.3 Resumo do estado

| Estado | Seções |
|---|---|
| Implementado | 7, 14, 15, 16, 17, 21, 22, 23, 25, 27, 32, 34, 36, 43, 44, 46 |
| Implementado parcialmente | 1, 3, 4, 5, 6, 8, 9, 11, 12, 13, 18, 20, 24, 26, 28, 29, 30, 31, 33, 35, 37, 38, 39, 40, 42, 47 |
| Planejado | 10, 19, 41, 48 |

---

# 54. Limitações Conhecidas e Riscos

Seção acrescentada na versão 2.0. Reúne o que as decisões, as specs (suposições e itens fora de escopo), o `PROGRESS.md` e a leitura do código registram como pendente. Cada item cita a seção do PRD afetada. Onde diz "não verificado", o código não permitiu confirmar o efeito.

## 54.1 Funcionais

| # | Tipo | Limitação ou risco | Seção | Origem |
|---|---|---|---|---|
| L-01 | Limitação | **`output.destination` e `OUTPUT_DELIVERY` nunca foram implementados.** O campo é aceito e ignorado; o consumidor só recebe o link no callback ou o obtém por download. | 6, 13, 30 | Spec 002, D-018 |
| L-02 | Limitação | `identityProofing.validations` no processo é só registrado: não executa validação nem bloqueia a assinatura. A sessão de proofing é independente e não há vínculo entre as duas. | 6, 9, 49 (item 4) | Specs 001, 005 |
| L-03 | Limitação | `FAILED`, `EXPIRED` e `SUSPENDED` existem, mas nenhum fluxo leva até eles. Não há expiração nem limite de tempo de assinatura; uma falha permanente deixa o processo no estado atual com `MANUAL_ACTION`. | 11, 12 | D-007, D-012 |
| L-04 | Risco | O polling do provider cria uma nova operação `PROVIDER_STATUS_CHECK` a cada 2 s enquanto o processo estiver pendente, sem limite: processos que nunca terminam geram operações sem fim. | 13 | D-013 |
| L-05 | Risco | **Os adapters de DocuSign e Lacuna não foram validados contra os sandboxes reais** (sem credenciais no desenvolvimento). Os mapeamentos vêm da documentação pública e foram testados só contra servidores falsos; os campos da Lacuna (upload, `flowActions`, nomes de status, `content?type=`) são os mais incertos. Os testes `LiveProviderTests` ficam pulados sem credenciais. | 18, 29, 50 | D-084, D-088, spec 011 |
| L-06 | Limitação | DocuSign e Lacuna não liberam por signatário: o documento só vai ao provider depois de todos os canais de todos os signatários confirmados e a ordem fica a cargo do provider. Assinatura avançada ou qualificada específica do fornecedor não é configurada. E-mail é obrigatório por signatário nesses providers. | 6, 29 | D-085, spec 011 |
| L-07 | Risco | A Lacuna pode duplicar o documento remoto se a conexão cair entre a criação remota e o commit local. | 21, 29 | Spec 011 |
| L-08 | Limitação | **O envio do código de confirmação só tem notificador simulado**: não existe canal real de e-mail, SMS ou WhatsApp. Também não há token de signatário: a aplicação consumidora repassa o código recebido. | 6 | D-075, spec 010 |
| L-09 | Limitação | Falha permanente do notificador deixa a confirmação em envio até o reprocessamento da operação. | 6, 16 | Spec 010 |
| L-10 | Limitação | Identity Proofing só tem adapter simulado; sem callback ao fim da sessão, sem expiração de sessão; reprocessar com `SIM-FLAKY` não garante sucesso. Evidências só em JSON base64. | 9 | Spec 005, D-047 |
| L-11 | Limitação | Não há circuit breaker (Fase 2) nem bulkhead por fornecedor; todos os providers compartilham a fila `signature-provider`. | 19, 20 | Constituição V, D-026 |
| L-12 | Limitação | Não há motor de políticas de Identity Proofing nem roteamento, fallback ou seleção dinâmica de provider (Fases 2 e 3). | 10, 47, 48 | Spec 005, D-082 |
| L-13 | Limitação | Link de download sem revogação e sem uso único: vale para vários downloads até expirar. A base da URL (`Artifacts:PublicBaseUrl`) está em `http://localhost:8080`; fora do ambiente local é preciso configurá-la. | 31 | D-016, D-061 |
| L-14 | Limitação | Uploads não consumidos não são removidos (vencem em 24 h só para uso). Documentos, evidências de processos, outbox, inbox, journal e registros de idempotência não têm retenção nem rotina de limpeza e crescem sem limite. | 22, 23, 24, 26, 38 | D-004, D-080 |
| L-15 | Limitação | Metadados nativos do provider preservados são mínimos (no DocuSign: `envelopeId`, status e contagem de signatários); não existem os artefatos `provider/completion-certificate.pdf` e `evidence/audit.json` do PRD original. | 26, 28 | Código |
| L-16 | Limitação | Não há evento `SIGNER_OPENED_DOCUMENT` nem evento de journal para falha de callback. | 24 | Código |
| L-17 | Limitação | A métrica `orchestrator.process.failed` conta `FAILED`, `REJECTED` e `EXPIRED`, mas só `REJECTED` ocorre; `Signature Conversion` não tem métrica própria. | 40 | Código |

## 54.2 Segurança e proteção de dados

| # | Tipo | Limitação ou risco | Seção | Origem |
|---|---|---|---|---|
| L-18 | Risco | **`Auth:Enabled` é falso por padrão no código.** Sem ligá-lo, a API é aberta, sem RBAC nem segregação, e o ator vem de um header sem verificação. O compose o liga. O Swagger fica sempre habilitado. | 37 | D-062 |
| L-19 | Limitação | TLS, criptografia em repouso, KMS, secrets manager, WAF e controle de egress são infraestrutura externa e não fazem parte do repositório. O compose usa HTTP e o realm de demonstração não exige TLS. | 37 | D-066, README |
| L-20 | Risco | Segredos de demonstração estão no compose, no realm do Keycloak e nos padrões de código (chave de links, segredo padrão de callbacks, chave de hash de códigos); a chave de hash de códigos não é sobrescrita no compose. Devem ser substituídos em qualquer ambiente real. | 37 | D-015, D-020, README |
| L-21 | Risco | Segredos de callbacks cadastrados ficam em texto no banco (exibidos uma vez na criação). | 8, 38 | D-034, DATA-PROTECTION |
| L-22 | Risco | A busca do documento por URL só tem proteção contra SSRF com `Artifacts:BlockPrivateNetworks=true`; o padrão é falso. | 8, 37 | D-019, D-035 |
| L-23 | Risco | Webhooks de provider não têm proteção contra replay e reconciliam de forma síncrona na requisição HTTP. | 18, 37 | Código |
| L-24 | Limitação | Rate limit só na criação de processo (por instância, janela fixa, 429 sem `Retry-After`); limite de callbacks por host também por instância. Não há limite de número de signatários nem de tamanho do corpo; o limite de upload efetivo pode ser menor que os 50 MiB configurados (nginx do portal: 20 MB; padrões do framework: não verificado). | 8, 37, 43 | D-066 |
| L-25 | Risco | Portal guarda token de acesso e refresh token em `sessionStorage` (exposição a XSS) e o nginx não define política de segurança de conteúdo. O logout não encerra a sessão no Keycloak. | 37 | D-069, D-091 |
| L-26 | Limitação | `DATA-PROTECTION.md` não cobre contatos de signatários, códigos de confirmação nem uploads, e não define retenção para documentos de processos. | 38 | D-048, D-074, D-079 |
| L-27 | Risco | O append-only do journal é imposto por trigger; quem for dono do banco pode removê-lo (não há papel de banco separado). | 24 | Código |

## 54.3 Operação, qualidade e documentação

| # | Tipo | Limitação ou risco | Seção | Origem |
|---|---|---|---|---|
| L-28 | Limitação | Sem alertas, dashboards, backend de traces e logs (só o `debug` do coletor) nem Prometheus no compose; métricas só no endpoint do coletor. `/health` só verifica o PostgreSQL; o compose não tem healthchecks para API, Worker e portal. | 39, 41, 42 | D-065, PROGRESS |
| L-29 | Limitação | A meta de 99,9% de disponibilidade não foi medida e depende de infraestrutura de produção. Dois limites (criação por cliente e callbacks por host) são por instância e não se somam entre réplicas. | 42 | Constituição |
| L-30 | Pendência | **Definition of Done de produção**: runbooks, testes de carga, testes de caos e testes de segurança dedicados (penetração, DAST, varredura de dependências) não existem. | 50 | PROGRESS |
| L-31 | Limitação | Portal testado só em jsdom com API simulada (sem E2E em navegador real); idioma misto; aba *Identity Proofing* só lista as capabilities pedidas; filtro de dead letters sem o domínio `notification`; não exibe o `correlationId` dos erros. | 34, 35 | D-081 |
| L-32 | Risco | A imagem do MinIO usada no compose é `bitnamilegacy/minio` porque as oficiais deixaram de ser baixáveis: risco de manutenção e de segurança. O código usa só a API S3, então o backend é trocável. | 26 | D-020 |
| L-33 | Limitação | Documentos de apoio com trechos desatualizados em relação ao código: README (serviços do compose, número de abas, formulário de login), spec 009 ("sem refresh token", superado por D-091), D-026 (o Worker consome as cinco filas) e o título de um teste do portal ("nine tabs", que verifica dez abas). | — | Revisão 2.0 |

---

# 55. Documentos Relacionados

| Documento | Conteúdo |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Detalhe técnico: projetos da solução, modelo de dados, filas e DLQs, outbox e inbox, workers, configuração por variáveis de ambiente, serviços do docker-compose e portas. |
| [DECISIONS.md](DECISIONS.md) | Decisões autônomas `D-001` a `D-094`, por spec. |
| [PROGRESS.md](PROGRESS.md) | Estado das specs, relatório de verificação e pendências. |
| [DATA-PROTECTION.md](DATA-PROTECTION.md) | Política de proteção de dados (Identity Proofing e artefatos). |
| `specs/001` a `specs/011` | Especificação, plano e tarefas de cada incremento. |
| `.specify/memory/constitution.md` | Princípios e restrições do projeto. |
| `README.md` | Como executar, URLs, usuários de demonstração e exemplos. |
| `/swagger` (API em execução) | OpenAPI gerado, com exemplos dos principais endpoints. |

**Sobre a numeração.** As seções 1 a 52 mantêm o número e o título da versão 1.0; as referências por número feitas em specs, decisões, comentários de código e testes (seções 6 a 18, 21 a 27, 30, 31, 33 a 41, 43, 45, 47 a 50 e 52) continuam válidas. Qualquer seção nova deve ser acrescentada ao final, depois da 55, sem renumerar.
