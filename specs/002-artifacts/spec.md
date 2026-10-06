# Feature Specification: Artefatos e Download Seguro

**Feature Branch**: `002-artifacts`

**Created**: 2026-10-06

**Status**: Draft

**Input**: User description: "Artefatos (PRD seções 25 a 27, 30, 31): armazenamento interno em object storage compatível com S3 com hash SHA-256, documento original e final, evidências, listagem e download por URL temporária assinada e auditada."

## Clarifications

### Session 2026-10-06

- Q: Como o consumidor identifica o artefato no pedido de link? → A: Corpo opcional `{"artifactId": "..."}` ou `{"type": "SIGNED_DOCUMENT"}`; sem corpo, o padrão é o documento assinado se o processo estiver concluído, senão o original.
- Q: A URL de download exige autenticação além da assinatura? → A: Não nesta spec; a assinatura HMAC com expiração é a autorização (OIDC fica na spec 008). O link é de uso múltiplo dentro da validade (single-use é opcional no PRD e fica fora).
- Q: Quem gera o conteúdo do documento assinado no ambiente simulado? → A: O adapter simulado produz o conteúdo (original + página de assinatura em texto) de forma determinística; o hash do assinado difere do original.
- Q: Qual a chave interna dos objetos? → A: `{processId}/input/original`, `{processId}/output/signed`, `{processId}/evidence/evidence.json`, num único bucket `signature-artifacts`; nunca exposta.
- Q: A entrega ao destino externo (`output.destination`) entra aqui? → A: Não; apenas preservação interna e download seguro. Entrega a destino externo fica fora desta spec.
- Q: Como a URL base do link é definida? → A: Configuração `Artifacts:PublicBaseUrl` (padrão `http://localhost:8080`), endpoint `GET /v1/downloads/{artifactId}?expires=...&sig=...`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Preservar o documento original e o final (Priority: P1)

Ao criar um processo, a plataforma obtém o documento da origem informada e o guarda internamente, registrando hash, tipo de conteúdo e tamanho. Ao concluir a assinatura, o documento assinado e uma evidência são obtidos do provider e também guardados internamente. A plataforma nunca depende de armazenamento externo como única cópia.

**Why this priority**: Princípio de system of record (constitution II) e base de todos os outros cenários.

**Independent Test**: Criar um processo, aguardar `COMPLETED` e verificar que existem artefatos de original, assinado e evidência, cada um com hash que confere com o conteúdo.

**Acceptance Scenarios**:

1. **Given** um processo criado com URL de documento acessível, **When** o fluxo processa o documento, **Then** o original é armazenado com SHA-256, tipo de conteúdo e tamanho, e o evento `DOCUMENT_STORED` registra o id e o hash do artefato.
2. **Given** a URL do documento inacessível ou com resposta de erro permanente (ex.: 404), **When** o fluxo tenta obtê-lo, **Then** a operação falha (sem armazenar nada) e o processo exige ação manual.
3. **Given** a assinatura concluída, **When** o fluxo finaliza, **Then** o documento assinado e a evidência são armazenados com hash e o evento `FINAL_DOCUMENT_STORED` é registrado.
4. **Given** a mesma operação executada duas vezes (entrega duplicada), **When** reprocessada, **Then** não surge um segundo artefato do mesmo tipo para o processo.
5. **Given** um documento maior que o limite configurado, **When** obtido, **Then** é rejeitado como erro permanente.

---

### User Story 2 - Consultar artefatos e evidências (Priority: P1)

O consumidor lista os artefatos de um processo (original, assinado, evidências) com seus metadados e hashes.

**Why this priority**: Critérios de aceite 9, 10 e 11 do PRD.

**Independent Test**: Concluir um processo e listar artefatos; verificar tipos, hash, tamanho e tipo de conteúdo.

**Acceptance Scenarios**:

1. **Given** um processo concluído, **When** o consumidor lista os artefatos, **Then** recebe original, documento assinado e evidência, cada um com id, tipo, content type, SHA-256, tamanho e data.
2. **Given** um processo ainda em andamento, **When** lista, **Then** recebe apenas os artefatos já armazenados.
3. **Given** processo inexistente, **When** lista, **Then** recebe não encontrado.
4. **Given** o resultado da listagem, **Then** nenhuma chave interna de armazenamento é exposta.

---

### User Story 3 - Download por URL temporária assinada (Priority: P1)

O consumidor solicita um link de download de um artefato específico e recebe uma URL assinada, de curta duração e válida para um único artefato. Ao usar a URL, o conteúdo é entregue e o acesso é auditado.

**Why this priority**: Critério de aceite 16 e requisito de segurança da constitution.

**Independent Test**: Solicitar o link do documento assinado, baixar o conteúdo, comparar o hash, e verificar a recusa após expirar ou com assinatura adulterada.

**Acceptance Scenarios**:

1. **Given** um artefato existente, **When** o consumidor solicita o link (informando o artefato ou o tipo desejado), **Then** recebe uma URL e o instante de expiração (curto, padrão 5 minutos).
2. **Given** uma URL válida, **When** acessada, **Then** o conteúdo é entregue, seu SHA-256 confere com o registrado e o evento `DOCUMENT_DOWNLOADED` é registrado.
3. **Given** a URL expirada, **When** acessada, **Then** é recusada (sem conteúdo).
4. **Given** a URL com assinatura, artefato ou expiração adulterados, **When** acessada, **Then** é recusada.
5. **Given** a URL de um artefato, **When** usada para outro artefato, **Then** é recusada (escopo de um único artefato).
6. **Given** a URL gerada, **Then** ela não contém chaves de acesso permanentes nem a chave interna do objeto.
7. **Given** o link solicitado para processo sem o artefato pedido, **Then** a resposta é não encontrado/conflito conforme o caso.
8. **Given** o tipo omitido na solicitação, **When** o processo está concluído, **Then** o link é do documento assinado; se não concluído, é do original.

---

### Edge Cases

- Falha de rede ao baixar o documento de origem: tratada como erro transitório, mas nesta spec sem retry (a política de retry é da spec 003): a operação falha e exige ação manual.
- Armazenamento indisponível: a operação falha sem perder o processo; nenhum artefato fica registrado sem o objeto correspondente.
- Origem com redirecionamentos ou destinos internos: nesta spec apenas http/https é aceito, com limites de tamanho e tempo. O bloqueio de destinos locais/privados (guarda SSRF) é entregue na spec 004 e reaproveitado aqui (configurável, desativado em desenvolvimento/testes).
- Conteúdo diferente do esperado (ex.: não é PDF): aceito, com o tipo de conteúdo registrado como informado pela origem.
- Artefato duplicado por reentrega: ignorado de forma idempotente.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: O sistema MUST armazenar artefatos em armazenamento de objetos compatível com S3, sob a estrutura `signature-artifacts/{processId}/{input|output|evidence|provider|identity}/...`.
- **FR-002**: Todo artefato MUST ter `artifactId`, tipo, content type, SHA-256, tamanho, data de criação e chave de objeto, registrados no momento do armazenamento; o hash MUST ser calculado sobre o conteúdo efetivamente gravado.
- **FR-003**: O documento original MUST ser obtido da URL informada na criação e armazenado antes de o processo avançar para validação; falhas MUST marcar a operação como falha.
- **FR-004**: O documento assinado e uma evidência MUST ser obtidos do provider (via adapter) e armazenados ao final do fluxo; o processo só vai a `COMPLETED` após ambos estarem armazenados.
- **FR-005**: O armazenamento de artefato MUST ser idempotente por processo e tipo (um artefato `ORIGINAL_DOCUMENT`, `SIGNED_DOCUMENT` e `EVIDENCE` por processo).
- **FR-006**: O sistema MUST expor `GET /v1/signature-processes/{id}/artifacts` sem expor chaves internas.
- **FR-007**: O sistema MUST expor `POST /v1/signature-processes/{id}/download-link` que retorna `url` e `expiresAt`; a URL MUST ser assinada (HMAC) com expiração curta configurável (padrão 5 min) e escopo de um único artefato.
- **FR-008**: O endpoint de download MUST validar assinatura, expiração e artefato antes de entregar o conteúdo em stream, com `Content-Type`, `Content-Length` e cabeçalho `X-Content-SHA256`.
- **FR-009**: Cada download concluído MUST gerar o evento de journal `DOCUMENT_DOWNLOADED` (artefato, hash, correlationId); a emissão do link MUST gerar `DOWNLOAD_LINK_ISSUED`.
- **FR-010**: URLs de download MUST NOT conter credenciais permanentes do armazenamento nem a chave do objeto.
- **FR-011**: A obtenção do documento MUST limitar o tamanho (padrão 50 MB), o tempo (padrão 30 s) e aceitar apenas http/https.
- **FR-012**: O ambiente local MUST incluir o armazenamento de objetos no docker compose, com o bucket criado automaticamente.
- **FR-013**: Um artefato MUST NOT ser registrado sem que o objeto exista no armazenamento (objeto gravado antes do registro; órfãos são aceitáveis, registros sem objeto não).
- **FR-014**: O hash servido MUST poder ser verificado pelo consumidor comparando com o SHA-256 listado nos artefatos.

### Key Entities

- **Artifact**: arquivo preservado de um processo; tipo (`ORIGINAL_DOCUMENT`, `SIGNED_DOCUMENT`, `EVIDENCE`), content type, SHA-256, tamanho, chave de objeto (interna), data.
- **DownloadLink**: autorização temporária e assinada para um único artefato (não persistida; auditada no journal).
- **ArtifactStore**: porta para o armazenamento de objetos.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% dos artefatos listados têm hash que confere com o conteúdo baixado.
- **SC-002**: 100% das URLs expiradas, adulteradas ou usadas em outro artefato são recusadas.
- **SC-003**: Um processo concluído sempre possui original, documento assinado e evidência armazenados (0 processos `COMPLETED` sem os três).
- **SC-004**: Todo download concluído deixa exatamente um registro de auditoria correspondente.
- **SC-005**: O fluxo completo com artefatos continua concluindo em até 30 segundos em ambiente local para documentos de até 5 MB.

## Assumptions

- Documento de origem acessível por URL http(s); upload direto fica fora de escopo.
- O provider simulado gera o documento assinado e a evidência de forma determinística a partir do original.
- Retry e DLQ para falhas de download/armazenamento são da spec 003; aqui falhas exigem ação manual.
- Autenticação e autorização dos endpoints são da spec 008; o endpoint de download autoriza pela assinatura da URL.
- Entrega do documento ao destino de saída (`output.destination`) e callbacks são tratados em spec posterior.
- Criptografia em repouso depende da configuração do armazenamento em produção; em desenvolvimento não é exigida.
