# Quickstart — 004

Pré-requisitos: Docker. O compose libera HTTP e rede privada para callbacks apenas para desenvolvimento (`Callbacks__AllowHttp`, `Callbacks__AllowPrivateNetworks`) e inclui um receptor de exemplo (`callback-sink`) que valida a assinatura e guarda os eventos recebidos.

1. `docker compose up -d --build`
2. Registrar o destino: `POST /v1/callbacks` `{"callbackId":"DEMO_CALLBACK","url":"http://callback-sink:8080/hook","secret":"demo-secret-0123456789"}`.
3. Criar um processo com `"callback":{"callbackId":"DEMO_CALLBACK"}` (ver spec 001) e aguardar `COMPLETED`.
4. `GET http://localhost:8081/events` (receptor de exemplo) lista os eventos recebidos e se a assinatura foi válida; `GET /v1/signature-processes/{id}/callbacks` mostra as entregas.
5. Falha do receptor: `externalId` com prefixo `SIM-` não é necessário; derrube o `callback-sink`, crie um processo e observe `RETRY_PENDING`/`DLQ` na entrega, sem alterar o estado do processo; reenvie com `POST /v1/signature-processes/{id}/retry` `{"operationId":"op_..."}`.
6. Testes: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
