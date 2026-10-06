# PRD — Plataforma de Orquestração de Assinaturas Digitais e Identity Proofing

**Versão:** 1.0  
**Status:** Draft  
**Tipo de Produto:** Plataforma Corporativa / API / Backoffice  
**Domínio:** Assinatura Eletrônica, Identidade Digital e Gestão de Documentos  
**Data:** Outubro/2026

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

# 5. Escopo Funcional

A plataforma será composta pelos seguintes domínios funcionais:

| Domínio | Responsabilidade |
|---|---|
| Signature Orchestration | Orquestrar processos de assinatura |
| Identity Proofing | Validar identidade dos signatários |
| Document Management | Receber e preservar documentos |
| Provider Integration | Integrar provedores externos |
| Workflow Engine | Controlar o processo |
| State Machine | Controlar estados |
| Event Journal | Registrar eventos |
| Artifact Management | Gerenciar artefatos |
| Callback Management | Notificar consumidores |
| Reprocessing | Reexecutar operações |
| Reconciliation | Corrigir inconsistências |
| Operations Portal | Acompanhamento operacional |
| Audit | Garantir rastreabilidade |

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

Payload conceitual:

```json
{
  "externalId": "CONTRACT-928182",

  "document": {
    "fileName": "contrato.pdf",
    "source": {
      "type": "URL",
      "url": "https://..."
    }
  },

  "signers": [
    {
      "externalId": "customer-123",
      "name": "João Silva",
      "document": "12345678900"
    }
  ],

  "identityProofing": {
    "validations": [
      "FACE_MATCH",
      "LIVENESS",
      "DOCUMENT_AUTHENTICITY"
    ]
  },

  "signature": {
    "type": "ADVANCED"
  },

  "callback": {
    "url": "https://cliente.exemplo.com/signatures/callback"
  },

  "output": {
    "destination": {
      "type": "URL",
      "url": "https://cliente.exemplo.com/documents"
    }
  }
}
```

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
    "id": "doc_18273",
    "downloadUrl": "https://documents.exemplo.com/..."
  }
}
```

Callbacks deverão possuir autenticação e garantia de integridade.

Headers esperados:

```http
X-Signature-Event-Id
X-Signature-Timestamp
X-Signature-Signature
```

A assinatura deverá utilizar mecanismo como HMAC-SHA256 ou equivalente.

Falhas de callback deverão possuir retry independente do processo principal.

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

---

# 9. Identity Proofing

A plataforma deverá permitir que o sistema consumidor determine quais evidências de identidade são necessárias.

Capacidades previstas:

| Capability | Descrição |
|---|---|
| PERSON_DATA | Validação cadastral |
| DOCUMENT_DATA | Extração dos dados do documento |
| DOCUMENT_AUTHENTICITY | Documentoscopia |
| DOCUMENT_OWNERSHIP | Documento pertence à pessoa |
| FACE_MATCH | Comparação facial |
| LIVENESS | Prova de vida |
| GOVERNMENT_BIOMETRIC_MATCH | Comparação com base oficial |
| PHONE_OWNERSHIP | Validação do telefone |
| EMAIL_OWNERSHIP | Validação do e-mail |
| DEVICE_RISK | Avaliação de dispositivo |
| IDENTITY_RISK | Score agregado de identidade |

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

Modelo:

```json
{
  "operationId": "op_938273",

  "processId": "sig_12345",

  "type": "SIGNED_DOCUMENT_DOWNLOAD",

  "status": "RETRY_PENDING",

  "attempt": 3,

  "maxAttempts": 8,

  "nextRetryAt": "2026-10-06T03:12:00Z"
}
```

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

---

# 23. Inbox Pattern

Workers deverão possuir controle de mensagens processadas.

Cada evento deverá possuir identificador único.

Eventos duplicados deverão ser ignorados de maneira segura.

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

---

# 30. Artifact Delivery

Ao final do processo, a plataforma deverá manter o documento final internamente.

Quando configurado, deverá também enviar ou disponibilizar o documento para o destino indicado pelo consumidor.

O armazenamento externo nunca deverá ser considerado a única cópia existente.

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

---

# 42. Requisitos Não Funcionais

## Disponibilidade

Target inicial:

```text
99,9%
```

A arquitetura deverá permitir evolução para patamares superiores.

## Escalabilidade

API e workers deverão ser stateless quando possível.

Scaling deverá ocorrer horizontalmente.

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

## Consistência

O sistema deverá operar preferencialmente utilizando:

```text
At-Least-Once Delivery
+
Idempotency
```

Não deverá depender de garantia distribuída de exactly-once.

## Durabilidade

Nenhuma operação confirmada pela API poderá ser perdida.

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

---

# 52. Definição do Produto

A Plataforma de Orquestração de Assinaturas deverá ser responsável pelo ciclo de vida integral de processos de assinatura, desde o recebimento e preservação do documento original, passando pela comprovação de identidade, execução da assinatura em provedores externos, controle do workflow, resiliência, reprocessamento e reconciliação, até a preservação das evidências e disponibilização segura do documento final.

A plataforma deverá tratar **Identity Proofing e Signature Orchestration como bounded contexts distintos**, permitindo que Identity Proofing seja utilizado por outros domínios corporativos independentemente da existência de um processo de assinatura.

O princípio central da solução será:

> **Os sistemas consumidores determinam as capacidades e evidências necessárias. A plataforma decide como executá-las, com qual fornecedor, como preservar as evidências e como garantir a conclusão resiliente do processo.**