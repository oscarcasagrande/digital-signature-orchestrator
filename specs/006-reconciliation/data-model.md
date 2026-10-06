# Data Model — 006

- **signature_process** (alterada): nova coluna `last_reconciled_at` (timestamptz, nula).
- **reconciliation_record**: id (`rec_*`, PK), process_id (max 40, índice com created_at), trigger (`SCHEDULED`|`MANUAL`, max 20), internal_status (max 40), provider_status (max 40, nulo), outcome (`CORRECTED`|`CONSISTENT`|`NOT_APPLICABLE`|`PROVIDER_ERROR`, max 20), resulting_status (max 40, nulo), details jsonb (sem dados pessoais: signatários assinados, erro resumido), created_at. Índice (outcome, created_at).

## Regras de comparação
| Interno | Provider | Resultado |
|---|---|---|
| SIGNATURE_IN_PROGRESS / PARTIALLY_SIGNED / READY_FOR_SIGNATURE | SIGNED | corrige → SIGNED, cria SIGNED_DOCUMENT_DOWNLOAD |
| READY_FOR_SIGNATURE / SIGNATURE_IN_PROGRESS | PARTIALLY_SIGNED | corrige → PARTIALLY_SIGNED |
| SIGNATURE_IN_PROGRESS / PARTIALLY_SIGNED / READY_FOR_SIGNATURE | REJECTED | corrige → REJECTED |
| qualquer não terminal | CANCELLED | corrige → CANCELLED |
| qualquer | PENDING | consistente |
| estado à frente do provider | qualquer | consistente (nada a fazer) |

## Resultado da operação manual
`ReconcileResult { processId, outcome, internalStatus, providerStatus, corrected, resultingStatus, error }`.

## Eventos de journal novos
`RECONCILIATION_DISCREPANCY_FOUND` {internal, provider, trigger}, `RECONCILIATION_CORRECTED` {from, to, trigger}, `RECONCILIATION_PROVIDER_ERROR` {error} (somente manual).

## Configuração
```
Reconciliation: { Enabled: true, IntervalSeconds: 60, StaleAfterSeconds: 120, BatchSize: 50 }
```
