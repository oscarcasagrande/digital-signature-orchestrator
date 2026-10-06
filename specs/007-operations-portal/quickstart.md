# Quickstart — 007

Pré-requisitos: Docker (e Node 20+ para desenvolver o portal).

1. `docker compose up -d --build` — o portal fica em <http://localhost:3000> (nginx), a API em :8080.
2. Crie alguns processos (spec 001); abra o portal: a lista mostra processo, documento, provider, assinantes, status e SLA. Informe seu identificador de operador na barra superior.
3. Abra um processo: cabeçalho, timeline e abas (Overview, Signers, Identity Proofing, Operations, Artifacts, Provider Metadata, Audit Trail, Callbacks, Errors).
4. Operações manuais: "Reconcile Provider", "Download Artifact" (aba Artifacts), "Cancel Process", "Retry"/"Reprocess" em uma operação com falha (ex.: processo com `externalId` `SIM-DOWN-x`) e "Retry Callback". Confira em Audit Trail os eventos com ator `OPERATOR`.
5. Página "Dead letters": filtros por domínio e pendentes/resolvidas, com link para o processo.
6. Desenvolvimento do portal: `cd portal && npm install && npm run dev` (proxy para `http://localhost:8080`); testes: `npm test`; build: `npm run build`.
7. Backend: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh` (inclui o portal servido pelo nginx e o proxy da API).

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
