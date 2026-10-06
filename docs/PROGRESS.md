# Progresso

Estado: **todas as specs (001 a 011) implementadas, verificadas e mescladas em `main`.**

## Specs
- [X] 001 Núcleo
- [X] 002 Artefatos
- [X] 003 Resiliência
- [X] 004 Callbacks
- [X] 005 Identity proofing
- [X] 006 Reconciliação básica
- [X] 007 Portal operacional
- [X] 008 Segurança e observabilidade
- [X] 009 Endurecimento de segurança
- [X] 010 Signatários, confirmação e progresso
- [X] 011 Providers reais em sandbox

Se o contexto reiniciar: ler este arquivo e docs/DECISIONS.md. Não há trabalho obrigatório pendente.

---

# Relatório final

## Como executar

`docker compose up -d --build`, depois `bash scripts/smoke-test.sh` (README tem as URLs, usuários de demonstração e exemplos). Testes: `dotnet test` (unitários e integração com Testcontainers, exige Docker) e `cd portal && npm test`.

## Verificação final

| Verificação | Resultado |
|---|---|
| Testes unitários (.NET) | 212 passando |
| Testes de integração (.NET, Postgres, RabbitMQ e MinIO reais) | 164 passando |
| Testes do portal (Vitest) | 59 passando; `npm run build` ok |
| Smoke test contra `docker compose` (todas as specs, Keycloak real, coletor OTel) | `SMOKE TEST PASSED` |

## O que foi entregue

- **001 Núcleo**: criação de processo com Idempotency-Key obrigatório, máquinas de estado de negócio e operacional independentes, operações, journal append-only, outbox transacional e inbox, APIs de consulta, provider simulado (falhas injetadas pelo prefixo do `externalId`).
- **002 Artefatos**: MinIO, SHA-256, documento original, assinado e evidência, download por URL temporária assinada.
- **003 Resiliência**: retry com backoff e jitter configurável por tipo de operação, classificação de erros, DLQ por domínio (tabela e fila `*-dlq`), reprocessamento a partir de uma operação.
- **004 Callbacks**: entrega assíncrona assinada com HMAC-SHA256, callbacks registrados, proteção contra SSRF, retry independente do estado do processo.
- **005 Identity proofing**: sessões e validações por capacidade com provider simulado, mascaramento de dados pessoais, retenção e remoção de evidências.
- **006 Reconciliação básica**: worker periódico e reconciliação manual comparando estado interno e do provider, com histórico.
- **007 Portal operacional**: lista com filtros, busca e paginação; detalhe com timeline e nove abas; operações manuais auditadas com a identidade do operador; página de dead letters; nginx com proxy.
- **008 Segurança e observabilidade**: OIDC com Keycloak (usuários e M2M), RBAC (viewer, operator, client, admin), segregação por cliente, ator de auditoria vindo do token, limite de criação por cliente, login no portal; OpenTelemetry (traces com propagação pelo outbox/RabbitMQ, métricas mínimas da seção 40, logs com TraceId/CorrelationId) exportado a um OpenTelemetry Collector com endpoint Prometheus.

## Critérios de aceite do MVP (PRD seção 49)

| # | Critério | Verificação |
|---|---|---|
| 1 | Enviar um documento | `ArtifactTests.Completed_process_has_original_signed_and_evidence_with_correct_hashes`; smoke "artifacts" |
| 2 | Criar processo de assinatura | `CreateProcessTests.First_creation_returns_202_and_replay_returns_200_with_same_process`; smoke "create" |
| 3 | Informar signatários | `QueryAndCancelTests.Get_process_returns_masked_signers_and_states`; validação em `CreateProcessTests` |
| 4 | Definir validações de identidade | `ProofingTests` (sessões e capacidades), payload `identityProofing.validations` em `WorkflowTests` |
| 5 | Definir callback | `CallbackTests.Dynamic_url_receives_signed_events_through_to_completion`, `Registered_callback_id_uses_the_registered_url_and_secret`; smoke "callbacks" |
| 6 | Consultar o estado | `QueryAndCancelTests`, `PortalApiTests.List_returns_the_columns_of_the_portal` |
| 7 | Receber atualizações por callback | `CallbackTests` (eventos assinados até a conclusão, cancelamento e rejeição); smoke "callbacks" |
| 8 | Acompanhar todas as operações | `QueryAndCancelTests.Operations_and_events_are_paginated`, `WorkflowTests.Process_reaches_completed_with_all_operations_and_journal_events`; smoke "operations and events" |
| 9 | Recuperar documento original | `ArtifactTests.Original_can_be_downloaded_by_type_and_equals_the_source_document`; smoke "secure download" |
| 10 | Recuperar documento assinado | `ArtifactTests.Default_link_is_the_signed_document_and_content_matches_the_listed_hash`; smoke "secure download" |
| 11 | Consultar evidências | Evidência listada com hash em `ArtifactTests.Completed_process_has_original_signed_and_evidence_with_correct_hashes`; evidências de proofing nunca baixáveis (`ProofingTests.Evidence_cannot_be_downloaded`) |
| 12 | Reprocessar uma operação | `ResilienceTests.Retry_resumes_from_the_failed_operation_without_repeating_completed_ones`, `Retry_after_a_permanent_failure_with_explicit_operation_id`; smoke "resilience" |
| 13 | Recuperar automaticamente de falhas transitórias | `ResilienceTests.Transient_provider_failures_are_retried_until_the_process_completes`, `Source_503_is_transient_and_recovers_when_the_source_recovers`; smoke "transient failure recovers" |
| 14 | Identificar operações em DLQ | `ResilienceTests.Exhausted_provider_operation_goes_to_the_provider_dlq_with_references_only`, `Dead_letter_listing_validates_domain_and_paginates`; página Dead letters (`DeadLetters.test.tsx`); smoke "DLQ" |
| 15 | Visualizar o processo no portal | `portal` (59 testes: lista, detalhe, abas, operações manuais, dead letters, login); smoke "operations portal" (nginx, SPA, proxy) |
| 16 | Baixar documento por URL temporária segura | `ArtifactTests` (`Tampered_signature_and_expiry_are_forbidden`, `Expired_link_is_gone_and_leaves_no_download_event`, `Link_is_scoped_to_a_single_artifact`); smoke "secure download" |

## Pendências e limites conhecidos

- Fora do escopo por definição: fases 2 e 3 do PRD (seções 47 e 48) e seção 45.
- Segurança: WAF, KMS, secrets manager e criptografia em repouso gerenciada ficam como infraestrutura externa (documentado no README). Os segredos do realm e do compose são apenas de demonstração. Callbacks registrados e sessões de identity proofing não são segregados por cliente (somente processos, listagens, dead letters e idempotência).
- Portal: login por formulário (password grant) em vez de redirecionamento OIDC; token em `sessionStorage`.
- Observabilidade: sem alertas (seção 41), sem dashboards prontos; traces e logs só no `debug` do coletor, métricas em Prometheus `:8889`. Circuit breaker e bulkhead continuam opcionais no MVP (obrigatórios na fase 2).
- Definition of Done de produção (seção 50) que depende de ambiente real: testes de carga, caos e segurança dedicados e runbooks não fazem parte do MVP.
- Docker Desktop caiu duas vezes no ambiente de desenvolvimento durante builds; foi reiniciado e os testes foram refeitos (sem impacto no código).

## Decisões tomadas

Todas registradas em `docs/DECISIONS.md` (D-001 a D-088), por spec. Destaques: retenção de idempotência de 24 h; operações de proofing reutilizam a tabela `operation`; DLQ como tabela e mensagem `*-dlq`; evidências nunca baixáveis; reconciliação registra apenas correções e execuções manuais; `Auth:Enabled` configurável (desligado mantém `X-Operator-Id`); papéis via `realm_access.roles` e segregação por `azp`; propagação de trace pelo payload do outbox; Keycloak 25 e coletor OTel em compose.

---

# Spec 009 - Endurecimento de segurança

Entregue (mesclada em `main`):
- Segregação por cliente de sessões de identity proofing, callbacks registrados, dead letters e idempotência de sessões; artefatos herdam o dono do processo (404 para não donos). Varredura de testes de integração em todas as rotas com id.
- Portal com authorization code + PKCE (S256) contra o Keycloak; client `portal` sem password grant; client `orchestrator-cli` para scripts e smoke.
- Postgres do host na porta 5433.

Verificação:
| Verificação | Resultado |
|---|---|
| Testes unitários (.NET) | 212 passando |
| Testes de integração (.NET) | 170 passando (execução final limpa) |
| Testes do portal (Vitest) | 67 passando; `npm run build` ok |
| Smoke test contra `docker compose` (PKCE, segregação de sessões e callbacks, porta 5433, Keycloak e coletor) | `SMOKE TEST PASSED` |


---

# Spec 010 - Signatários, confirmação e progresso

Entregue (mesclada em `main`):
- **API de signatários**: `name`, `document`, `email`, `phone`, `signatureType`, `order`, `confirmation` por pessoa; `defaults` no processo; payload anterior compatível; validação com erro em `signers[i].campo` (canal sem contato, CPF, formatos, `order` sem lacunas).
- **Confirmação por canal**: capability `IConfirmationNotifier` (sem fornecedor) com notificador simulado e sink consultável; código de 6 dígitos, validade, limite de tentativas, reenvio com rate limit; envio como operação com retry/DLQ (domínio `notification`), confirmação registrada como operação, eventos no journal; só o HMAC do código é gravado, nunca em claro em logs, eventos ou outbox; assinatura liberada no provider só após confirmação e, com `order`, na vez do signatário.
- **Progresso**: `progress { completedSteps, totalSteps, currentStep }` (lista) e `steps` (detalhe), derivado do plano fixado na criação; etapas canceladas/falhas não contam.
- **Portal**: coluna *Etapas* (X/Y, barra, etapa atual ao passar o mouse), aba *Etapas* por signatário, página *Novo processo* com upload e tabela de signatários.
- Upload de documento (`POST /v1/document-uploads`), Swagger com exemplos, smoke test e README atualizados.

Verificação:
| Verificação | Resultado |
|---|---|
| Testes unitários (.NET) | 262 passando |
| Testes de integração (.NET, Postgres, RabbitMQ e MinIO reais) | 198 passando |
| Testes do portal (Vitest) | 81 passando; `npm run build` ok |
| Smoke test contra `docker compose` (inclui spec 010: upload, confirmação sequencial, progresso, ausência do código no journal, Swagger) | `SMOKE TEST PASSED` |

Limites conhecidos: sem token de signatário (a aplicação consumidora repassa o código); uploads não consumidos não são removidos; falha permanente do notificador deixa a confirmação em envio até reprocessar a operação; operadores não criam processos (RBAC mantido), logo o formulário do portal exige papel `client` ou `admin`.

---

# Spec 011 - Providers reais em sandbox

Entregue (mesclada em `main`):
- Adapters **DocuSign** (JWT Grant, envelope, status, void, PDF combinado e Certificate of Completion) e **Lacuna Signer** (`X-Api-Key`, upload, fluxo por signatário, status, cancelamento, conteúdo assinado e relatório), atrás de `IProviderAdapter`.
- Seleção por `Provider__Default` (simulado é o padrão); `ProviderRouter` mantém cada processo no provider em que nasceu.
- Credenciais só de variáveis de ambiente: `.env` (fora do git e do Docker), `.env.example` documentado, compose repassa.
- Webhooks assinados (`/v1/webhooks/docusign`, `/v1/webhooks/lacuna`), públicos e fora do Swagger; só disparam reconciliação, o provider é a verdade.
- Com confirmação por código, o documento só vai ao provider depois que todos confirmam.

Verificação:
| Verificação | Resultado |
|---|---|
| Testes unitários (.NET) | 278 passando |
| Testes de integração (.NET; servidores falsos de DocuSign e Lacuna em processo) | 220 passando, 2 pulados (testes `Live*` sem credenciais) |
| Smoke test contra `docker compose` | `SMOKE TEST PASSED` |

**Não verificado**: nenhuma chamada foi feita aos sandboxes reais (sem credenciais no ambiente). Os mapeamentos HTTP vêm da documentação pública e foram testados só contra servidores falsos; os campos da Lacuna (upload, `flowActions`, nomes de status, `content?type=`) são os mais incertos. Rode `LiveProviderTests` com credenciais para validar. Limites: sem liberação por signatário nos providers reais; assinatura avançada/qualificada específica de cada fornecedor não é configurada; a Lacuna pode duplicar o documento remoto se a conexão cair entre a criação remota e o commit local.

---

# Correção: criação de processos por pessoas e renovação do token

Problema reproduzido no `docker compose`: com token de `operator`, `POST /v1/document-uploads` e `POST /v1/signature-processes` respondiam 403 (as regras só permitiam `client` e `admin`), então o formulário do portal falhava para operadores. O portal também não renovava o token: guardava só o `access_token` e, ao expirar, descartava a sessão e voltava ao login.

Entregue:
- `client`, `operator` e `admin` criam processo e enviam documento; `viewer` segue com 403 e não vê o botão (D-089, D-092).
- Dono e ator: processo criado por pessoa não tem cliente dono (invisível a clientes de API), grava o usuário como ator `OPERATOR` no journal e usa Idempotency-Key escopada por usuário (D-090).
- Portal renova o token com o refresh token (antes de expirar, em segundo plano e ao receber 401), sem perder a tela (D-091).

Verificação:
| Verificação | Resultado |
|---|---|
| Testes unitários (.NET) | 278 passando |
| Testes de integração (.NET; inclui `HumanCreationTests`: matriz de papéis nos dois endpoints, ator, dono, idempotência, uploads) | 240 passando, 2 pulados (testes `Live*` sem credenciais) |
| Testes do portal (renovação e papéis) | 102 passando; `npm run build` ok |
| Smoke test contra `docker compose` (inclui criação com token de operator pelo caminho do portal, viewer 403, renovação por refresh token) | `SMOKE TEST PASSED` |
