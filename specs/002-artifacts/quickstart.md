# Quickstart — 002

Pré-requisitos: Docker. O documento de origem precisa ser uma URL http(s) acessível pelo worker (no compose, use por exemplo `https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf` ou um servidor local acessível via `host.docker.internal`).

1. `docker compose up -d --build` (sobe também `minio` em :9000, console em :9001, usuário/senha locais `minioadmin`).
2. Criar um processo (ver spec 001) e aguardar `COMPLETED`.
3. `GET /v1/signature-processes/{id}/artifacts` → original, assinado e evidência com `sha256`, `size`, `contentType`.
4. `POST /v1/signature-processes/{id}/download-link` (corpo opcional `{"type":"ORIGINAL_DOCUMENT"}`) → `{url, expiresAt}`.
5. `curl -OJ "<url>"` e conferir `sha256sum` com o listado; repetir após a expiração → `410`; alterar `sig` → `403`.
6. Testes: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
