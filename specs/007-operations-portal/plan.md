# Implementation Plan: Portal Operacional

**Branch**: `007-operations-portal` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

Duas frentes: (1) backend — novos endpoints de operação (lista filtrável com SLA, metadados do provider auditados, filtro de dead letters por processo), identificação do operador (`X-Operator-Id` → `ActorContext` → journal) e evento `RECONCILIATION_REQUESTED`; (2) frontend — aplicação React 18 + Vite + TypeScript em `portal/` (lista, detalhe com timeline e 9 abas, operações manuais com confirmação, dead letters), servida por nginx com proxy `/v1` para a API. Verificação por testes de integração do backend, testes Vitest/Testing Library do portal e checagem do nginx no smoke test.

## Technical Context

**Language/Version**: C# 12 / .NET 8 (backend); TypeScript 5 / React 18 / Vite 5 (portal), Node 20+ para build
**Primary Dependencies**: backend sem novos pacotes; portal: react, react-dom, react-router-dom 6; dev: vite, @vitejs/plugin-react, typescript, vitest, jsdom, @testing-library/react, @testing-library/user-event, @testing-library/jest-dom
**Storage**: PostgreSQL — `signature_process.document_file_name`
**Testing**: xUnit + Testcontainers (backend); Vitest + Testing Library com `fetch` mockado (portal)
**Target Platform**: navegador moderno; Docker Compose (serviço `portal` com nginx na porta 3000)
**Project Type**: web-service + worker + web frontend
**Performance Goals**: lista de 50 processos e detalhe em < 2 s local
**Constraints**: sem conteúdo de artefato nem CPF completo na UI; mesma origem (sem CORS)
**Scale/Scope**: 3 páginas, 9 abas, 7 operações manuais

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | UI exibe apenas o código neutro do adapter (`provider`); nenhum fornecedor específico. PASS |
| II System of record | Portal só consome a API; nada de acesso direto a banco/filas. PASS |
| III Idempotência | Operações manuais usam endpoints idempotentes/seguros da plataforma; conflitos exibidos. PASS |
| IV Outbox | Ações manuais seguem os fluxos existentes (estado + outbox na mesma transação). PASS |
| V Operações | Retry/Reprocess/Reconcile/Cancel/Retry Callback expostos como ações do PRD seção 36. PASS |
| VI Auditoria | Toda ação manual gera evento com ator `OPERATOR`; `RECONCILIATION_REQUESTED` e `PROVIDER_METADATA_INSPECTED` novos. PASS |
| VII Segurança/testes | Sem dados sensíveis na UI; operador identificado; testes automatizados. Autenticação OIDC/RBAC na spec 008 (registrada como pendência explícita). PASS (escopo) |
| Technology Constraints | Portal em React + Vite + TypeScript, executa no docker compose. PASS |

Sem violações. Re-check pós-design: PASS.

## Project Structure

```text
src/
├── Orchestrator.Domain/Entities/ SignatureProcess.DocumentFileName
├── Orchestrator.Application/Abstractions/ ActorContext
├── Orchestrator.Application/Processes/ ProcessQueries (ListAsync, ProviderInfo), SlaOptions, ProcessDto estendido
├── Orchestrator.Application/Resilience/ DeadLetterQueries (processId)
├── Orchestrator.Application/** handlers manuais passam a usar ActorContext
├── Orchestrator.Api/ ActorMiddleware, endpoints (lista, provider)
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddPortalSupport
portal/
├── package.json, vite.config.ts, tsconfig.json, index.html, nginx.conf, Dockerfile
├── src/ main.tsx, App.tsx, api/ (client.ts, types.ts), pages/ (ProcessList, ProcessDetail, DeadLetters),
│        components/ (Tabs, Timeline, ConfirmDialog, StatusBadge, JsonView, OperatorBar, ...), styles.css
└── src/__tests__/ (vitest + RTL)
tests/ IntegrationTests (PortalApiTests, ActorTests)
docker-compose.yml (+portal)
```

**Structure Decision**: frontend isolado em `portal/` (sem acoplamento ao build .NET); backend segue a Clean Architecture existente.
