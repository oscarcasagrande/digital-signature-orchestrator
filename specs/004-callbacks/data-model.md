# Data Model — 004

- **callback_registration**: callback_id (PK, max 100, padrão `^[A-Za-z0-9_.-]+$`), url (max 2000, obrigatório), secret (max 200, obrigatório, nunca exposto após a criação), active (bool), description (max 300, opcional), created_at, updated_at.
- **callback_delivery**: id (`cbd_*`, PK), process_id FK, operation_id (único), event_id (`evt_*`, único, estável), event_type (max 80, ex.: `SIGNATURE_PROCESS.COMPLETED`), process_status (max 40, snapshot), destination (max 2100; `callbackId` ou URL sem query/credenciais), status (`PENDING`|`RETRY_PENDING`|`DELIVERED`|`FAILED`|`DLQ`, max 20), attempts (int), last_status_code (int, nulo), last_error (max 500, nulo), occurred_at, created_at, delivered_at (nulo). Índice (process_id, created_at).

## Payload do evento
`{ eventId, eventType, processId, externalId, status, occurredAt, document?: { id, downloadUrl } }` — `document` apenas em `SIGNATURE_PROCESS.COMPLETED`.

## Cabeçalhos
`X-Signature-Event-Id` = eventId · `X-Signature-Timestamp` = segundos Unix · `X-Signature-Signature` = `sha256=` + hex(HMAC-SHA256(segredo, "{timestamp}.{corpo}")) · `Content-Type: application/json` · `User-Agent: SignatureOrchestrator/1.0`.

## Transições da entrega
PENDING → DELIVERED | RETRY_PENDING (transitório) | FAILED (permanente) | DLQ (esgotada); RETRY_PENDING → DELIVERED | RETRY_PENDING | DLQ; FAILED/DLQ → PENDING (reprocesso manual).

## Eventos de journal novos
`CALLBACK_REQUESTED` {deliveryId, eventType, destination}, `CALLBACK_DELIVERED` {deliveryId, eventId, statusCode, attempts}.

## Configuração
```
Callbacks: { DefaultSecret, AllowPrivateNetworks: false, AllowHttp: false, AllowedHosts: [], TimeoutSeconds: 10, RateLimitPerSecond: 10, SignatureToleranceSeconds: 300 }
Artifacts: { BlockPrivateNetworks: false }
```
