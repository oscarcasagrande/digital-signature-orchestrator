# Data Model — 005

- **proofing_session**: id (`prf_*`, PK), external_id (max 200, obrigatório), status (`WAITING_EVIDENCE`|`IN_PROGRESS`|`COMPLETED`, max 30), result (`PENDING`|`APPROVED`|`REJECTED`, max 20), subject jsonb (name max 300, document CPF 11 dígitos, phone 10–15 dígitos opcional, email max 200 opcional, deviceId max 100 opcional), correlation_id, created_at, updated_at, completed_at (nulo), evidence_deleted_at (nulo).
- **identity_validation**: id (`ivl_*`, PK), session_id FK, capability (max 40), required (bool), status (`WAITING_EVIDENCE`|`PENDING`|`PASSED`|`FAILED`|`ERROR`, max 20), score (double, 0 a 1, nulo), details jsonb (sem dados pessoais), provider_code (max 40, nulo), operation_id (nulo), created_at, updated_at, completed_at (nulo). Único (session_id, capability).
- **artifact** (reuso): `process_id` = id da sessão; tipos novos `DOCUMENT_FRONT`, `DOCUMENT_BACK`, `SELFIE`.
- **operation** (reuso): tipo `IDENTITY_VALIDATION`, `input = {validationId}`; FK para `signature_process` removida.

## Pré-requisitos por capacidade
| Capacidade | Evidências | Dados do sujeito |
|---|---|---|
| PERSON_DATA | — | name, document |
| DOCUMENT_DATA, DOCUMENT_AUTHENTICITY, DOCUMENT_OWNERSHIP | DOCUMENT_FRONT | — |
| LIVENESS, GOVERNMENT_BIOMETRIC_MATCH | SELFIE | — |
| FACE_MATCH | SELFIE, DOCUMENT_FRONT | — |
| PHONE_OWNERSHIP | — | phone |
| EMAIL_OWNERSHIP | — | email |
| DEVICE_RISK | — | deviceId |
| IDENTITY_RISK | todas as outras validações terminadas | — |

## Estados
Validação: WAITING_EVIDENCE → PENDING (operação criada) → PASSED | FAILED | ERROR; ERROR → PENDING (reprocesso). Sessão: WAITING_EVIDENCE (alguma validação aguarda evidência) | IN_PROGRESS → COMPLETED (todas PASSED/FAILED). Resultado: PENDING até COMPLETED; REJECTED se obrigatória FAILED; senão APPROVED.

## Eventos de journal (aggregate id = sessão)
`PROOFING_SESSION_CREATED`, `IDENTITY_VALIDATION_REQUESTED` {validationId, capability, operationId}, `EVIDENCE_RECEIVED` {type, sha256, size}, `IDENTITY_VALIDATED` {validationId, capability, status, score}, `PROOFING_SESSION_COMPLETED` {result}, `EVIDENCE_DELETED` {reason, evidence:[{type, sha256}]}; mais retry/DLQ/reprocesso da spec 003.

## Configuração
```
Identity: { MaxEvidenceBytes: 5242880, EvidenceRetentionDays: 30, RetentionSweepMinutes: 60 }
```
