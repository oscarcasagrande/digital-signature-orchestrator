# Quickstart — 003

Pré-requisitos: Docker. Para ver o retry rápido, suba o compose com `Retry__Default__DelaysSeconds__*` curtos (ver README) ou use os testes automatizados.

1. `docker compose up -d --build`
2. Falha transitória que se recupera: criar processo com `externalId` `SIM-FLAKY-2-abc` → `PROVIDER_CREATE_PROCESS` falha 2 vezes (`RETRY_PENDING`, `nextRetryAt` no futuro) e depois conclui; `GET .../operations` mostra `attempt` 3.
3. DLQ: criar com `externalId` `SIM-DOWN-abc` → após esgotar tentativas a operação fica `DLQ`, o processo `operationalStatus=DLQ` e `GET /v1/dead-letters?domain=signature-provider` lista a entrada.
4. Reprocessar: `POST /v1/signature-processes/{id}/retry` (corpo opcional `{"operationId":"op_...","reason":"..."}`); como a causa persiste em `SIM-DOWN-`, use um processo cuja causa foi corrigida (ex.: origem de documento que voltou).
5. Testes: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
