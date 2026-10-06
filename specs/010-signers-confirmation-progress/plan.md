# Implementation Plan: Signatários, confirmação e progresso

**Spec**: [spec.md](spec.md)

## Design

- **Validação** (`CreateProcessValidator`): signatário ganha `email`, `phone`, `signatureType`, `order`, `confirmation`; `defaults` no processo; `externalId` do signatário e `signature` do processo viram opcionais (o tipo efetivo precisa existir em algum nível). Uma função `SignerPlan.Resolve` (pura, testável) produz os signatários efetivos usados pela validação e pelo handler.
- **Dados** (migration `AddSignerConfirmation`): `signer` + `email`, `phone`, `signature_type`, `sign_order`, `confirmation_channels`, `released_at`; tabela `signer_confirmation` (um por signatário e canal: status PLANNED/SENDING/SENT/CONFIRMED/LOCKED, `code_hash`, `expires_at`, `failed_attempts`, `send_count`, `last_sent_at`, `confirmed_at`, token de concorrência `version`); tabela `document_upload`; tabela `notification_sink` (somente notificador simulado).
- **Capability de notificação** (`IConfirmationNotifier`, Application): `SendAsync(channel, destination, code, attempt)`; sem tipos de fornecedor. `SimulatedConfirmationNotifier` grava no `IConfirmationSink` (tabela `notification_sink`, consultável também em `GET /v1/dev/confirmation-codes/{processId}` quando `Confirmation:ExposeSink=true`, somente admin). Falhas injetadas por prefixo do destino (`sim-down`, `sim-flaky-N`).
- **Serviço** (`ConfirmationService`): geração (6 dígitos, `RandomNumberGenerator`), HMAC-SHA256 do código (`Confirmation:HashKey`), `VerifyAsync` (expiração → 410, bloqueio → 423, errado → 422 com tentativas restantes, retry de concorrência, grava operação `CONFIRMATION_VERIFY` e journal) e `ResendAsync` (intervalo mínimo e teto de envios → 429).
- **Workflow**: novos tipos `CONFIRMATION_SEND` (domínio `notification`, fila e DLQ próprias) e `CONFIRMATION_VERIFY`. `PROVIDER_STATUS_CHECK` passa a avançar signatários (`SignerAdvancer`): libera por ordem, cria `CONFIRMATION_SEND` para confirmações PLANNED e, com tudo confirmado, chama `provider.ReleaseSignerAsync`. O provider simulado, em modo "gated" (processo com confirmação ou ordem), só assina signatários liberados, `SignDelaySeconds` depois da liberação; sem gate o comportamento é o anterior.
- **Progresso** (`ProgressCalculator`, função pura): entrada = fatos (artefato original, signatários, confirmações, status do processo, operações com falha, entrega do callback final, se há callback); saída = etapas ordenadas, `completedSteps`, `totalSteps`, `currentStep`. `ProcessQueries` carrega os fatos em lote para a listagem.
- **Upload**: `POST /v1/document-uploads` (multipart, até `Artifacts:MaxDocumentBytes`), dono = cliente do token; `document.source.type=UPLOAD` lido do store pelo `DOCUMENT_DOWNLOAD`.
- **Segurança**: papéis inalterados (criar e enviar documento: client/admin; confirmar e reenviar: operate); `/v1/dev/*` só admin; e-mail/telefone mascarados; código nunca em journal, operação, outbox ou log.
- **Portal**: `ProcessList` com coluna Etapas; `ProcessDetail` com aba Etapas por signatário e confirmações; página `NewProcess` (upload, tabela de signatários, defaults) com erros por signatário/campo.
- **Swagger**: exemplos de requisição/resposta via `SwaggerGen` (filtro de esquema/operação com JSON de exemplo).

## Testes

- Unitários: validador (herança, `[]`, canal sem contato, CPF, ordem com lacuna/mista), `SignerPlan`, `ConfirmationService` (expiração, tentativas, bloqueio, reenvio), `ProgressCalculator`, provider simulado gated, mascaramento.
- Integração: criação com defaults e legado, confirmação ponta a ponta com sink, sequencial, retry do notificador, DLQ, ausência do código em journal/operações/outbox, progresso na lista e detalhe, cancelamento, upload e segregação das rotas novas.
- Portal (Vitest): coluna Etapas, detalhe, formulário.
- Smoke: fluxo com confirmação via sink de desenvolvimento, progresso, upload.
