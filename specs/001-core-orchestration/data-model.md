# Data Model — 001

Tabelas em snake_case; timestamps `timestamptz` UTC.

- **signature_process**: id (text PK `sig_*`), external_id (text, required, max 200), business_status, operational_status, signature_type (SIMPLE|ADVANCED|QUALIFIED), request jsonb, callback jsonb, identity_validations jsonb, correlation_id, version (concurrency token), created_at, updated_at, completed_at null.
- **signer**: id, process_id FK, external_id (max 200), name (required, max 300), document (CPF, 11 dígitos), position, signed (bool), signed_at null.
- **operation**: id (`op_*`), process_id FK, type, status (NOT_STARTED|PROCESSING|COMPLETED|FAILED|CANCELLED), attempt, max_attempts, next_retry_at null, input jsonb (referências), output jsonb, error jsonb, sequence, created_at, updated_at. Unique (process_id, type, sequence).
- **journal_event**: id (`evt_*`), process_id, seq (bigserial), type, occurred_at, actor_type, actor_id, metadata jsonb, correlation_id, causation_id. Trigger bloqueia UPDATE/DELETE.
- **outbox_event**: id, aggregate_id, type, payload jsonb (apenas ids), queue, available_at, published_at null, attempts. Índice parcial `published_at IS NULL`.
- **inbox_message**: consumer, message_id, received_at; PK (consumer, message_id).
- **idempotency_record**: key PK (max 128), request_hash, process_id, created_at.
- **provider_process**: process_id PK, provider_code, provider_process_id, external_reference (unique), normalized_status, metadata jsonb, updated_at.

## Máquina de negócio
CREATED→DOCUMENT_RECEIVED→VALIDATING→READY_FOR_SIGNATURE→SIGNATURE_IN_PROGRESS→(PARTIALLY_SIGNED→)SIGNED→FINALIZING→COMPLETED. De qualquer não-terminal → FAILED, CANCELLED, EXPIRED; SIGNATURE_IN_PROGRESS/PARTIALLY_SIGNED → REJECTED. Terminais (COMPLETED, FAILED, CANCELLED, EXPIRED, REJECTED) não saem.

## Máquina operacional
READY→PROCESSING; PROCESSING→READY|RETRY_PENDING|SUSPENDED|DLQ|MANUAL_ACTION; RETRY_PENDING→PROCESSING; SUSPENDED/MANUAL_ACTION/DLQ→READY. Independente da de negócio.

## Operações e mapeamento
DOCUMENT_DOWNLOAD→DOCUMENT_RECEIVED · DOCUMENT_STORE→VALIDATING (simulado) · PROVIDER_CREATE_PROCESS→READY_FOR_SIGNATURE · PROVIDER_SEND_DOCUMENT→SIGNATURE_IN_PROGRESS · PROVIDER_STATUS_CHECK (reagenda até assinado; →PARTIALLY_SIGNED/SIGNED/REJECTED) · SIGNED_DOCUMENT_DOWNLOAD→FINALIZING · SIGNED_DOCUMENT_STORE→COMPLETED.
