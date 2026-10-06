# Quickstart — 005

Pré-requisitos: Docker. Identity Proofing funciona sem nenhum processo de assinatura.

1. `docker compose up -d --build`
2. Abrir uma sessão:
   `POST /v1/proofing-sessions` (cabeçalho `Idempotency-Key`) com `{"externalId":"KYC-1","subject":{"name":"Maria","document":"12345678909"},"validations":[{"type":"PERSON_DATA","required":true},{"type":"LIVENESS","required":true},{"type":"FACE_MATCH","required":false}]}` → `202` com `sessionId` e status `WAITING_EVIDENCE`.
3. Enviar a selfie: `POST /v1/proofing-sessions/{id}/biometrics` com `{"type":"SELFIE","contentType":"image/jpeg","content":"<base64>"}` e a frente do documento: `POST .../documents` com `{"type":"DOCUMENT_FRONT",...}`.
4. `GET /v1/proofing-sessions/{id}/result` → `status=COMPLETED`, `result=APPROVED`, validações com `score`.
5. Falha simulada: selfie com o texto `FAKE_SPOOF` no conteúdo → `LIVENESS` `FAILED` → `REJECTED`; `externalId` `SIM-FLAKY-2-x` → falha transitória que se recupera; `SIM-DOWN-x` → DLQ em `GET /v1/dead-letters?domain=identity-proofing` e `POST .../retry`.
6. Excluir evidências: `DELETE /v1/proofing-sessions/{id}/evidence` (sessão concluída).
7. Testes: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
