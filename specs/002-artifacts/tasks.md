# Tasks: Artefatos e Download Seguro

**Input**: `/specs/002-artifacts/` (plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md)
**Tests**: incluídos para fluxos críticos (hash, assinatura de URL, workflow com MinIO), antes da implementação.

## Phase 1: Setup

- [X] T001 Adicionar pacote AWSSDK.S3 em `src/Orchestrator.Infrastructure` e Testcontainers.Minio em `tests/Orchestrator.IntegrationTests`
- [X] T002 [P] Adicionar `minio` (porta 9000/9001, credenciais locais de desenvolvimento) e `minio-init` ao `docker-compose.yml` e variáveis `Artifacts__*`/`S3__*` em api e worker

## Phase 2: Foundational

- [X] T003 [P] Entidade `Artifact` (id `art_*`; type max 40; content_type max 200; sha256 64 hex; size ≥ 0; storage_key; file_name max 300; metadata jsonb) e enum `ArtifactType` em `src/Orchestrator.Domain/Entities/Artifact.cs`
- [X] T004 Porta `IArtifactStore` (`PutAsync`, `GetAsync`, `ExistsAsync`, `EnsureBucketAsync`) em `src/Orchestrator.Application/Artifacts/IArtifactStore.cs`
- [X] T005 `IOrchestratorDb.Artifacts`, mapeamento (índice único process_id+type) e migration `AddArtifacts` em `src/Orchestrator.Infrastructure/Persistence/`
- [X] T006 `S3ArtifactStore`, `S3Options` e inicializador de bucket (hosted service em API e Worker) em `src/Orchestrator.Infrastructure/Storage/`

## Phase 3: User Story 1 — Preservar original e final (P1)

**Independent Test**: processo concluído tem original, assinado e evidência com hash conferido.

- [X] T007 [P] [US1] Testes unitários de `ArtifactService` (hash por streaming correto, limite de tamanho, idempotência por processo+tipo) com store em memória em `tests/Orchestrator.UnitTests/ArtifactServiceTests.cs`
- [X] T008 [P] [US1] Testes unitários do `FakeProviderAdapter` para download do assinado/evidência (determinístico, difere do original) em `tests/Orchestrator.UnitTests/FakeProviderAdapterTests.cs`
- [X] T009 [P] [US1] Testes de integração (Postgres+RabbitMQ+MinIO+origem HTTP local): fluxo completo com artefatos, 404 na origem, documento acima do limite, em `tests/Orchestrator.IntegrationTests/ArtifactWorkflowTests.cs`
- [X] T010 [US1] `ArtifactService` (StoreAsync com SHA-256 streaming, limite 50 MB, chave por tipo, registro idempotente) em `src/Orchestrator.Application/Artifacts/ArtifactService.cs`
- [X] T011 [US1] `IDocumentFetcher` e `HttpDocumentFetcher` (timeout 30 s, limite 50 MB, só http/https, erro → `DocumentFetchException`) em `src/Orchestrator.Application/Artifacts/` e `src/Orchestrator.Infrastructure/Storage/`
- [X] T012 [US1] Estender `IProviderAdapter` com `DownloadSignedDocumentAsync`/`DownloadEvidenceAsync` e implementar no `FakeProviderAdapter` em `src/Orchestrator.Application/Providers/IProviderAdapter.cs` e `src/Orchestrator.Infrastructure/Providers/FakeProviderAdapter.cs`
- [X] T013 [US1] Atualizar `WorkflowEngine` (DOCUMENT_DOWNLOAD/STORE e SIGNED_DOCUMENT_DOWNLOAD/STORE com artefatos reais, eventos com id e hash, falha → FAILED) em `src/Orchestrator.Application/Workflow/WorkflowEngine.cs`; ajustar testes da spec 001 que usam `example.com`

## Phase 4: User Story 2 — Consultar artefatos (P1)

- [X] T014 [P] [US2] Testes de integração de `GET /artifacts` (tipos, hash, sem chave interna, 404) em `tests/Orchestrator.IntegrationTests/ArtifactApiTests.cs`
- [X] T015 [US2] `ArtifactQueries` e endpoint `GET /v1/signature-processes/{id}/artifacts` em `src/Orchestrator.Application/Artifacts/ArtifactQueries.cs` e `src/Orchestrator.Api/Endpoints/ArtifactEndpoints.cs`

## Phase 5: User Story 3 — Download seguro (P1)

- [X] T016 [P] [US3] Testes unitários de `DownloadLinkSigner` (válida, expirada, adulterada, outro artefato, sem chave permanente na URL) em `tests/Orchestrator.UnitTests/DownloadLinkSignerTests.cs`
- [X] T017 [P] [US3] Testes de integração do link e do download (conteúdo e hash, 410 expirado, 403 adulterado/outro artefato, journal DOWNLOAD_LINK_ISSUED/DOCUMENT_DOWNLOADED) em `tests/Orchestrator.IntegrationTests/DownloadTests.cs`
- [X] T018 [US3] `DownloadLinkSigner` (HMAC-SHA256, Base64Url, FixedTimeEquals, expiração configurável padrão 5 min) em `src/Orchestrator.Application/Artifacts/DownloadLinkSigner.cs`
- [X] T019 [US3] `DownloadService` (emitir link, validar e abrir stream com auditoria) e endpoints `POST .../download-link` e `GET /v1/downloads/{artifactId}` em `src/Orchestrator.Application/Artifacts/` e `src/Orchestrator.Api/Endpoints/ArtifactEndpoints.cs`

## Phase 6: Polish

- [X] T020 [P] Atualizar `scripts/smoke-test.sh` (artefatos, hash, link, expiração/adulteração) usando origem de documento própria no compose
- [X] T021 [P] Atualizar `README.md` e `specs/002-artifacts/quickstart.md`
- [X] T022 Executar `dotnet build`, `dotnet test`, `docker compose up` e smoke; corrigir falhas

## Dependencies

Setup → Foundational → US1 → US2 e US3 (independentes entre si após US1). Testes [P] antes da implementação de cada story.

## Implementation Strategy

MVP = US1 (preservação) + US3 (download). US2 (listagem) é leve e entra junto.
