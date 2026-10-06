# Data Model — 007

- **signature_process** (alterada): nova coluna `document_file_name` (text, max 300, nula) — preenchida na criação (`document.fileName`) e retroalimentada pela migration a partir de `request`.

## Contratos de leitura novos
- `ProcessListItem { processId, externalId, documentFileName, provider (código do adapter ou null), signersSigned, signersTotal, businessStatus, operationalStatus, createdAt, updatedAt, sla: "OK" | "ALERT" }` em `PagedResult` (`items`, `page`, `pageSize`, `total`).
- `ProcessDto` (detalhe) ganha `documentFileName`, `provider`, `sla`.
- `ProviderInfo { provider, providerProcessId, externalReference, normalizedStatus, metadata (JSON nativo), updatedAt }`.
- Dead letters: filtro `processId`.

## Regra de SLA
`ALERT` se `businessStatus ∈ {FAILED, REJECTED, EXPIRED}` ou `operationalStatus ∈ {DLQ, MANUAL_ACTION}` ou (não terminal e `now - createdAt > Sla:SigningMinutes`); caso contrário `OK`.

## Ator
`ActorContext { Type, Id }`: padrão `CONSUMER`/`api`; com `X-Operator-Id` válido → `OPERATOR`/`<id>`.

## Eventos de journal novos
`RECONCILIATION_REQUESTED` {trigger} (toda reconciliação manual), `PROVIDER_METADATA_INSPECTED` {provider}.

## Modelo do portal (TypeScript)
Tipos espelham os DTOs da API (`ProcessListItem`, `ProcessDetail`, `Operation`, `JournalEvent`, `Artifact`, `CallbackDelivery`, `DeadLetter`, `ProviderInfo`, `ReconcileResult`, `ApiError`). Estado persistido apenas em `localStorage`: `operatorId`.

## Configuração
```
Sla: { SigningMinutes: 60 }
```
