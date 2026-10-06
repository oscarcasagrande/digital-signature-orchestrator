# Research — 001

- **Decision**: Fluxo orientado a operações encadeadas; cada operação concluída cria a próxima e um evento de outbox `OPERATION_REQUESTED{operationId}`. **Rationale**: operações independentes e retentáveis (base da spec 003). **Alternatives**: saga em memória (perde retomada), Temporal/Elsa (complexidade).
- **Decision**: Espera da assinatura via `PROVIDER_STATUS_CHECK` que se reagenda com `available_at` na outbox (polling a cada N s). **Rationale**: sem webhook nesta spec; reutilizado por retry (003) e reconciliação (006). **Alt.**: delay exchange do RabbitMQ (plugin extra).
- **Decision**: Outbox publisher no Worker com `SELECT ... FOR UPDATE SKIP LOCKED` e publisher confirms. **Rationale**: várias instâncias seguras, at-least-once. **Alt.**: LISTEN/NOTIFY.
- **Decision**: Inbox `inbox_message(consumer, message_id)` com PK, inserida na mesma transação do efeito; duplicata viola PK e é descartada.
- **Decision**: Idempotência: `idempotency_record(key PK, request_hash, process_id, created_at)`; hash SHA-256 do JSON canônico; concorrência resolvida por PK (insert-first). Retenção 24h: registro expirado é substituído.
- **Decision**: Journal append-only garantido por trigger Postgres que levanta exceção em UPDATE/DELETE. **Alt.**: só convenção de código.
- **Decision**: Migrations EF Core aplicadas no start da API (Worker aguarda o schema). **Rationale**: "um comando" no compose.
- **Decision**: IDs com prefixo (`sig_`, `op_`, `evt_`) + ULID/GUID curto.
- **Decision**: Testes de integração com Testcontainers (Docker disponível); sem mock de banco.
- **Decision**: Exchange direct `orchestrator.commands`, fila `signature-provider` durável com DLX `signature-provider-dlq` declarada (uso efetivo na spec 003).
