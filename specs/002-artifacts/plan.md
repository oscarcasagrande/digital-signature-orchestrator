# Implementation Plan: Artefatos e Download Seguro

**Branch**: `002-artifacts` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

Adiciona preservação interna de artefatos em MinIO (API S3) com hash SHA-256 calculado por streaming, e download por URL assinada (HMAC-SHA256, expiração curta, escopo de um artefato). O `WorkflowEngine` passa a baixar o original da URL informada (HttpClient com limite de tamanho/tempo) e a armazená-lo; ao final obtém documento assinado e evidência do adapter e os armazena antes de concluir.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: AWSSDK.S3 (ForcePathStyle), existentes (EF Core/Npgsql, RabbitMQ.Client)
**Storage**: PostgreSQL (tabela `artifact`, metadados) + MinIO bucket `signature-artifacts` (objetos)
**Testing**: xUnit + Testcontainers (Postgres, RabbitMQ, MinIO) + servidor HTTP local (Kestrel) como origem do documento
**Target Platform**: Docker Compose (minio + minio-init)
**Project Type**: web-service + worker (inalterado)
**Performance Goals**: fluxo < 30 s com documentos ≤ 5 MB
**Constraints**: documento ≤ 50 MB, timeout 30 s, link 5 min; objeto gravado antes do registro
**Scale/Scope**: 3 artefatos por processo

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Download do assinado/evidência via `IProviderAdapter` neutro. PASS |
| II System of record | Original, assinado e evidência com SHA-256, content type e tamanho gravados internamente. PASS |
| III Idempotência | Artefato único por (processo, tipo) com índice único; reexecução reaproveita. PASS |
| IV Outbox | Inalterado; registros de artefato entram na mesma transação do avanço do fluxo. PASS |
| V Operações | Operações existentes (DOCUMENT_DOWNLOAD/STORE, SIGNED_*) passam a ter efeito real; falhas → FAILED/MANUAL_ACTION (retry na 003). PASS |
| VI Auditoria | `DOCUMENT_STORED`, `FINAL_DOCUMENT_STORED`, `DOWNLOAD_LINK_ISSUED`, `DOCUMENT_DOWNLOADED` no journal. PASS |
| VII Segurança | URL assinada curta, sem chaves permanentes, escopo por artefato, comparação em tempo constante. PASS |

Sem violações. Re-check pós-design: PASS.

## Project Structure

```text
specs/002-artifacts/ plan.md research.md data-model.md quickstart.md contracts/openapi.yaml tasks.md
src/
├── Orchestrator.Domain/Entities/Artifact.cs, ArtifactType
├── Orchestrator.Application/Artifacts/ IArtifactStore, ArtifactService, DownloadLinkSigner, ArtifactQueries, DocumentFetcher port
├── Orchestrator.Application/Providers/IProviderAdapter.cs (+ DownloadSignedDocumentAsync, DownloadEvidenceAsync)
├── Orchestrator.Infrastructure/Storage/ S3ArtifactStore, S3Options, bucket initializer, HttpDocumentFetcher
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddArtifacts
├── Orchestrator.Api/Endpoints/ artifacts, download-link, GET /v1/downloads/{artifactId}
tests/ UnitTests (signer, hash/service with fake store, adapter content) · IntegrationTests (MinIO + origin server)
docker-compose.yml (+minio, minio-init)
```

**Structure Decision**: mesma Clean Architecture; porta `IArtifactStore` na Application e adapter S3 na Infrastructure.
