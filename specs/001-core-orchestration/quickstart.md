# Quickstart — 001

Pré-requisitos: Docker, .NET 8 SDK.

1. `docker compose up -d --build`
2. `curl -X POST localhost:8080/v1/signature-processes -H "Idempotency-Key: k1" -H "Content-Type: application/json" -d @specs/001-core-orchestration/contracts/sample-request.json` → `202` com `processId`.
3. Repetir o comando → `200`, mesmo `processId`.
4. `GET /v1/signature-processes/{id}/status` até `COMPLETED` (< 30 s).
5. `GET .../operations` e `.../events` mostram a trilha.
6. `POST .../cancel` em novo processo antes de concluir → `CANCELLED`.
7. Testes: `dotnet test` (requer Docker). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml); modelo: [data-model.md](data-model.md).
