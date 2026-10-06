# Implementation Plan: Providers reais em sandbox

**Spec**: [spec.md](spec.md)

## Design

- **Contrato**: `ProviderSigner` ganha `Email`, `Document` e `Order` (opcionais); `IProviderAdapter` ganha `SupportsPerSignerReleaseAsync` (simulado: sim; reais: não); `IProviderWebhookHandler` valida a assinatura e extrai a referência.
- **Roteamento**: `ProviderRouter` (Infrastructure) é o `IProviderAdapter` que o sistema enxerga. Criação → `ProviderSelection.Default` (de `Provider:Default`); demais chamadas → `provider_process.provider_code`. Adapters resolvidos sob demanda (credenciais só exigidas de quem for usado).
- **Credenciais**: `DotEnv.Load()` no início de API e Worker; adapters leem variáveis planas `DOCUSIGN_*` / `LACUNA_*` da configuração (que inclui o ambiente). `.env` ignorado pelo git e pelo Docker; `.env.example`; compose repassa as variáveis.
- **DocuSign**: `DocuSignTokenProvider` (JWT Grant RS256, cache); `DocuSignAdapter` (envelope em `created` com documento e destinatários, busca por `orchestratorRef` para idempotência, envio = `status: sent`, status por `GET envelope?include=recipients`, void, `documents/combined` e `documents/certificate`).
- **Lacuna**: `LacunaAdapter` (`X-Api-Key`; upload + criação do documento com um `flowAction` por signatário no envio; status, cancelamento, conteúdo assinado e relatório de assinaturas).
- **Confirmação e ordem**: sem liberação por signatário, `SignerAdvancer` envia os códigos a todos e só chama `SendDocumentAsync` após tudo confirmado (`PROVIDER_SEND_DEFERRED` no journal); ordem vai para `routingOrder`/`step`.
- **Webhooks**: `POST /v1/webhooks/{docusign|lacuna}` (público no `AccessMiddleware`, fora do Swagger): assinatura, depois `PROVIDER_WEBHOOK_RECEIVED`, depois `ReconciliationService.ReconcileAsync(trigger=WEBHOOK)`. 401 sem efeito se inválida; 200 `ignored` se desconhecido.
- **Erros**: `ProviderHttp` classifica (401/408/429/5xx/rede: transitório; demais 4xx: permanente), mensagens truncadas e sem credenciais.

## Testes

- Unitários: `.env`, opções e chaves, JWT RS256, handlers de webhook (HMAC e segredo).
- Integração (Testcontainers + servidores falsos em processo `FakeVendors`): ponta a ponta DocuSign e Lacuna, idempotência, retry/permanente, confirmação adiando o envio, cancelamento, rejeição, webhooks (assinatura, gatilho, público com auth ligada), reconciliação manual, provider padrão simulado, processos que mantêm o provider, segredos fora de logs/eventos/Swagger.
- Opcionais: `LiveProviderTests` contra os sandboxes (`LiveDocuSignFact`/`LiveLacunaFact` pulam sem credenciais).
- Smoke: padrão simulado, webhooks 401/404, `.env` ignorado, `.env.example`.
