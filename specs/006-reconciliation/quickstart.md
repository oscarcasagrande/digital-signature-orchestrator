# Quickstart — 006

Pré-requisitos: Docker.

1. `docker compose up -d --build` (o Worker agora também executa o reconciliador; intervalo e obsolescência padrão: 60 s e 120 s — o compose usa 10 s e 20 s para demonstração).
2. Criar um processo (spec 001) e, enquanto ele aguarda a assinatura, chamar `POST /v1/signature-processes/{id}/reconcile`: o resultado é `CONSISTENT` (provider ainda pendente), `NOT_APPLICABLE` (ainda sem registro no provider) ou `CORRECTED` (provider já assinou) — nunca altera nada sem divergência.
3. `GET /v1/signature-processes/{id}/reconciliations` lista as ações manuais e as divergências corrigidas.
4. Divergência real: nos testes automatizados o cenário "consulta/webhook perdidos" é criado diretamente no banco; o reconciliador leva o processo a `COMPLETED` (ver `tests/Orchestrator.IntegrationTests/ReconciliationTests.cs`).
5. Testes: `dotnet test` (Docker necessário). Smoke: `bash scripts/smoke-test.sh`.

Contrato: [contracts/openapi.yaml](contracts/openapi.yaml). Modelo: [data-model.md](data-model.md).
