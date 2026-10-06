# Política de proteção de dados (Identity Proofing e artefatos)

Dados biométricos (selfie) e documentos de identificação são **dados pessoais sensíveis**. Esta política torna explícitas as regras exigidas pela constitution e pelo PRD (seção 38).

| Tema | Política | Onde é aplicada |
|---|---|---|
| **Finalidade** | Evidências são usadas somente para executar as validações pedidas na sessão de proofing (finalidade única). Nenhuma outra sessão ou processo lê evidências de outra. | `IdentityValidationRunner` lê evidências apenas da própria sessão |
| **Minimização** | A plataforma guarda somente o necessário: tipo, hash SHA-256, tamanho, tipo de conteúdo e o resultado (score e motivo genérico, sem dados pessoais). Mensagens de fila e DLQ carregam apenas identificadores. | `artifact`, `identity_validation.details`, outbox |
| **Retenção** | Evidências de sessões concluídas são mantidas no máximo `Identity:EvidenceRetentionDays` (padrão 30 dias após a conclusão). O resultado e o journal permanecem. | `EvidenceRetentionService` (Worker) + `EvidencePurger.PurgeExpiredAsync` |
| **Exclusão** | O consumidor pode excluir as evidências de uma sessão concluída a qualquer momento (`DELETE /v1/proofing-sessions/{id}/evidence`). Objetos e registros são removidos; os hashes ficam no journal (`EVIDENCE_DELETED`). | `EvidencePurger.PurgeAsync` |
| **Controle de acesso** | Evidências **não** podem ser baixadas pela API (sem link de download); apenas metadados e hash são consultáveis. Autenticação/autorização (OIDC e RBAC) e segregação por cliente: spec 008. | API de proofing |
| **Criptografia** | Em trânsito: TLS no ambiente de produção. Em repouso: criptografia do armazenamento de objetos e do banco, com chaves em KMS no ambiente de produção; em desenvolvimento, equivalentes locais. | Infraestrutura (constitution: Security & Data Protection) |
| **Mascaramento** | CPF (últimos 2 dígitos), telefone (últimos 2 dígitos) e e-mail (primeiro caractere + domínio) mascarados em respostas e logs; conteúdo de evidências nunca é registrado. | `SensitiveMasker`, `ProofingQueries` |
| **Auditoria** | Criação, evidências recebidas (com hash), resultados, conclusão, exclusões e reprocessamentos ficam no journal append-only com ator e `correlationId`. | `journal_event` |
| **Segredos** | Segredos de callback ficam no armazenamento transacional neste MVP e são exibidos uma única vez; em produção devem residir em um gerenciador de segredos/KMS. | `callback_registration` |

Revisão: esta política deve ser revista a cada nova capacidade biométrica e antes de produção (Definition of Done do PRD, seção 50).
