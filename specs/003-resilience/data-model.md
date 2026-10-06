# Data Model — 003

- **operation** (alterada): `status` ∈ `NOT_STARTED`, `PROCESSING`, `RETRY_PENDING`, `DLQ`, `COMPLETED`, `FAILED`, `CANCELLED` (max 30); nova coluna `error_class` (`TRANSIENT`|`PERMANENT`|`UNKNOWN`, max 20, nula).
- **dead_letter_entry**: id (`dlq_*`, PK), process_id FK, operation_id FK (único enquanto não resolvida), operation_type (max 60), domain (max 40; `signature-provider`|`artifact`|`identity-proofing`|`callback`), queue (max 80, ex.: `artifact-dlq`), error_class (max 20), reason (max 500, resumo sem dados pessoais), attempts (int), created_at, resolved_at (nulo), resolved_by (max 100, nulo). Índice (domain, resolved_at).

## Política (configuração)
```
Retry:
  Jitter: 0.2
  UnknownMaxAttempts: 3
  Default: { MaxAttempts: 8, DelaysSeconds: [0, 5, 30, 120, 600, 1800, 7200, 21600], BaseDelaySeconds: 5, Multiplier: 6, MaxDelaySeconds: 21600 }
  Operations: { <OPERATION_TYPE>: { MaxAttempts, DelaysSeconds } }
```
Espera após a falha da tentativa n = `DelaysSeconds[n]` (índice 0 = primeira tentativa imediata), com o último valor repetido se a lista acabar.

## Transições de operação
NOT_STARTED → (executa) → COMPLETED | RETRY_PENDING (transitório com tentativas restantes) | FAILED (permanente) | DLQ (esgotada). RETRY_PENDING → executa na hora de `next_retry_at`. FAILED/DLQ/RETRY_PENDING → NOT_STARTED (reprocesso manual). NOT_STARTED/RETRY_PENDING → CANCELLED (cancelamento do processo).

## Estados do processo
Falha transitória: operacional `RETRY_PENDING`; esgotada: `DLQ`; permanente: `MANUAL_ACTION`; sucesso ou reprocesso: `READY`. O estado de negócio não muda por falhas.

## Eventos de journal novos
`OPERATION_RETRY_SCHEDULED` {operationId, attempt, nextRetryAt, errorClass}, `OPERATION_DEAD_LETTERED` {operationId, deadLetterId, domain}, `OPERATION_REPROCESS_REQUESTED` {operationId, reason}; `OPERATION_FAILED` ganha `errorClass`.
