# Implementation Plan: Callbacks Assinados e Seguros

**Branch**: `004-callbacks` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

## Summary

Cada mudança relevante de estado de negócio de um processo com destino de callback cria uma entrega (`callback_delivery`) e uma operação independente `CALLBACK_SEND` na fila `callback`. O `WorkflowEngine` executa a entrega: monta o payload (sem PII), assina com HMAC-SHA256, envia por um `CallbackSender` protegido por `SsrfGuard` (IP resolvido validado e usado na conexão, sem redirects, timeout, rate limit) e registra o resultado. Falhas usam o retry/DLQ da spec 003 sem tocar o estado do processo. Registros por `callbackId` e consulta de entregas completam a API.

## Technical Context

**Language/Version**: C# 12 / .NET 8
**Primary Dependencies**: existentes; `System.Net.Http.SocketsHttpHandler` (ConnectCallback), sem pacotes novos
**Storage**: PostgreSQL — `callback_registration`, `callback_delivery`
**Testing**: xUnit unit (signer, guard, rate limiter) + Testcontainers com receptor HTTP Kestrel local que valida a assinatura
**Target Platform**: Docker Compose (worker consome também a fila `callback`)
**Project Type**: web-service + worker
**Performance Goals**: callback de conclusão < 10 s após a conclusão
**Constraints**: nunca PII no corpo; segredo nunca em respostas/logs; padrão seguro de rede
**Scale/Scope**: rate limit por host em memória (por instância)

## Constitution Check

| Princípio | Avaliação |
|---|---|
| I Provider-agnostic | Callback não expõe fornecedor; payload só com dados da plataforma. PASS |
| II System of record | Entregas e tentativas persistidas; documento final via link temporário. PASS |
| III Idempotência | `eventId` estável; inbox e guarda de status evitam reenvio por duplicata; o receptor deduplica por `eventId`. PASS |
| IV Outbox/filas | Entrega criada na mesma transação da mudança de estado e publicada via outbox na fila `callback`; DLQ `callback-dlq`. PASS |
| V Operações/retry | `CALLBACK_SEND` é operação independente com retry/DLQ (spec 003). PASS |
| VI Auditoria | `CALLBACK_REQUESTED`, `CALLBACK_DELIVERED` no journal. PASS |
| VII/Segurança | HMAC-SHA256 + headers exigidos, replay (timestamp), HTTPS, SSRF (IP privado/loopback/link-local/metadata, DNS rebinding, redirects, timeout, rate limit, allowlist). PASS |

Sem violações. Re-check pós-design: PASS.

## Project Structure

```text
src/
├── Orchestrator.Domain/Entities/ CallbackRegistration, CallbackDelivery (+ DeliveryStatus)
├── Orchestrator.Application/Callbacks/ CallbackOptions, CallbackSigner, SsrfGuard (política), CallbackEmitter, CallbackQueries, CallbackAdmin (registro), ICallbackSender
├── Orchestrator.Application/Workflow/ WorkflowEngine (CALLBACK_SEND, emissão nos Move), ReprocessHandler/CancelProcessHandler (ajustes)
├── Orchestrator.Infrastructure/Callbacks/ HttpCallbackSender (ConnectCallback + rate limiter), GuardedHttp (handler compartilhado com o fetcher de documentos)
├── Orchestrator.Infrastructure/Persistence/Migrations/ AddCallbacks
├── Orchestrator.Api/Endpoints/ CallbackEndpoints
tests/ UnitTests (CallbackSignerTests, SsrfGuardTests, RateLimiterTests) · IntegrationTests (CallbackTests com receptor Kestrel)
```

**Structure Decision**: mesma Clean Architecture; política de rede e assinatura na Application (puras, testáveis), E/S HTTP na Infrastructure.
