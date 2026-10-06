# Data Model — 002

- **artifact**: id (`art_*`, PK), process_id FK, type (`ORIGINAL_DOCUMENT`|`SIGNED_DOCUMENT`|`EVIDENCE`, max 40), content_type (max 200), sha256 (64 hex chars), size (bigint, ≥ 0), storage_key (text, interno, nunca exposto), file_name (max 300), metadata jsonb, created_at. Índice único (process_id, type).

## Chaves de objeto (bucket `signature-artifacts`)
`{processId}/input/original` · `{processId}/output/signed` · `{processId}/evidence/evidence.json`

## Operações (efeito real)
- DOCUMENT_DOWNLOAD: baixa da URL (limite 50 MB / 30 s) para o store (grava objeto + calcula hash); registra `artifact` ORIGINAL_DOCUMENT; output da operação = {artifactId, sha256, size}. Falha → operação FAILED.
- DOCUMENT_STORE: valida a existência/hash do original no store e registra `DOCUMENT_STORED` (id+hash), avançando para VALIDATING.
- SIGNED_DOCUMENT_DOWNLOAD: obtém assinado e evidência do adapter; grava objetos e registra `artifact` SIGNED_DOCUMENT e EVIDENCE; business FINALIZING.
- SIGNED_DOCUMENT_STORE: confirma os dois artefatos e `FINAL_DOCUMENT_STORED`; business COMPLETED.

## Eventos de journal novos
`DOWNLOAD_LINK_ISSUED`, `DOCUMENT_DOWNLOADED` (metadata: artifactId, type, sha256).
