<!--
Sync Impact Report (temporary; remove before commit)
- Version change: 1.0.0 → 1.1.0
- Modified principles: IV (bulkhead enforcement phased; queue/DLQ isolation kept from MVP),
  V (circuit breaker and bulkhead mandatory from Phase 2, PRD section 47)
- Added sections: Technology Constraints
- Modified sections: Security & Data Protection Constraints (KMS and 99.9% availability scoped
  to production, local equivalents accepted in development; storage bullet moved to Technology
  Constraints)
- Removed sections: none
- Deferred items: none.
- Source of truth for derivation: docs/PRD.md v1.0
-->
# Digital Signature Orchestrator Constitution

## Core Principles

### I. Provider-Agnostic, Capability-Based Contracts
Consumers MUST request capabilities (e.g. `FACE_MATCH`, `LIVENESS`, signature type) or named
policies, never specific vendors. No public API, event, callback payload, or domain model MAY
expose or require knowledge of DocuSign, Clicksign, Adobe Sign, D4Sign, Unico, Serasa, Datavalid,
or any other vendor. Every vendor integration MUST live behind a Provider Adapter that implements
the common interface and MUST NOT leak vendor types into the domain. Native vendor metadata MUST
be preserved (JSONB) alongside a normalized status.
Rationale: vendors are executors of capabilities, not owners of the process; swapping vendors must
not require consumer changes.

### II. Platform as System of Record (Self-Contained)
The platform owns the signature process and MUST internally preserve every artifact needed to
prove and reconstruct it: original document, signed document, identity evidence, certificates,
and audit data. External storage or vendor storage MUST NEVER be the only copy. Every artifact
MUST carry a SHA-256 hash, content type, and size, recorded at storage time.
Identity Proofing and Signature Orchestration are distinct bounded contexts; Identity Proofing
MUST be usable without a signature process.

### III. Idempotency & At-Least-Once Delivery (NON-NEGOTIABLE)
The system operates on at-least-once delivery plus idempotency; it MUST NOT depend on distributed
exactly-once guarantees. Every repeatable operation MUST be idempotent using `processId`,
`operationId`, `idempotencyKey`, and `externalReference`. Create endpoints MUST require an
`Idempotency-Key`. Provider adapters MUST check for an equivalent existing operation before
creating a new one. Workers MUST use the Inbox pattern to safely ignore duplicate events.

### IV. Transactional Outbox & Event-Driven Asynchrony
External processing MUST be asynchronous via queues/topics. State changes and their events MUST
be committed atomically (state + `outbox_event` in one transaction) and published by an Outbox
Publisher; direct publish-after-commit is forbidden. Messages and DLQ entries MUST carry
references only, never full documents or sensitive data. Each external integration domain MUST
have its own queue/DLQ so one vendor's failure does not degrade others (bulkhead enforcement
follows Principle V). No operation confirmed by the API MAY be lost.

### V. Explicit State Machines, Operations & Resilient Recovery
Business status and operational status MUST be modeled as independent state machines. Every
orchestrator activity MUST be recorded as an independent, individually retryable Operation.
Failures MUST be classified (TRANSIENT, PERMANENT, UNKNOWN): transient errors retry with
exponential backoff plus jitter (configurable per operation type); permanent errors go to
FAILED or MANUAL_ACTION; unknown errors retry a bounded number of times, then DLQ. Reprocessing
MUST resume from the failed operation, not restart the process. Each external integration MUST
have an independent circuit breaker, and provider, identity, callback, and artifact workloads
MUST be isolated by bulkheads. Both are OPTIONAL in the MVP and MANDATORY from Phase 2
(PRD section 47). Webhooks MUST NOT be the sole source of provider state:
a Reconciliation Worker MUST complement them.

### VI. Auditability & Observability
All relevant actions, including every manual operator action, MUST append to an immutable
event journal carrying actor, timestamp, `correlationId`, and `causationId`. Components MUST emit
logs, metrics, and traces (OpenTelemetry preferred) tagged with correlation ID, process ID,
operation ID, and provider ID, so a process is traceable end to end. Required metrics and alerts
listed in the PRD (DLQ size, circuit breaker state, callback error rate, queue lag, SLA
violations, etc.) MUST be implemented before production.

### VII. Secure by Design & Test-Backed Delivery
Security and data protection MUST be designed in from the start (see Security & Data Protection
Constraints). Critical flows (idempotency, retry, DLQ, reconciliation, state transitions, adapter
contracts, callback signing) MUST have automated tests written alongside or before the
implementation; adapters MUST be covered by contract tests. Code MUST favor the simplest design
that satisfies the spec; added complexity MUST be justified in the plan.

## Security & Data Protection Constraints

- Authentication/authorization: OAuth2/OIDC, machine-to-machine auth, RBAC, and per-client data
  segregation are REQUIRED.
- Callbacks MUST be HMAC-SHA256 (or stronger) signed with `X-Signature-Event-Id`,
  `X-Signature-Timestamp`, and `X-Signature-Signature` headers, with replay protection, and
  retried independently of the main process. Pre-registered `callbackId` destinations are
  preferred; dynamic URLs MUST enforce HTTPS, block localhost and private ranges, defend against
  DNS rebinding, limit redirects, apply timeouts and rate limits, and use egress control.
- Downloads MUST use short-lived, single-artifact signed URLs with audit logging. Permanent keys
  MUST NEVER appear in URLs.
- Encryption in transit and at rest and a Secrets Manager are REQUIRED. KMS-managed keys are
  REQUIRED in the production environment; development MAY use a local equivalent (e.g. locally
  managed keys or a KMS emulator), which MUST NOT be used in production. Secrets MUST NOT be
  committed to the repository.
- Biometric data is sensitive personal data. Retention, deletion, access control, data
  minimization, and purpose limitation policies MUST be explicit; sensitive values MUST be masked
  in logs.
- API and workers MUST be stateless and independently horizontally scalable. The 99.9%
  availability target is a requirement of the production environment only; local and development
  environments are not bound by it.
- Public APIs MUST be versioned (`/v1/...`) and documented (API First).

## Technology Constraints

- API and workers: .NET.
- Operations Portal: React with Vite and TypeScript.
- Transaction store: PostgreSQL with JSONB.
- Messaging / event bus: RabbitMQ.
- Artifact store: MinIO. Code MUST use the S3-compatible API so production object storage
  remains swappable.
- The complete system (API, workers, portal, PostgreSQL, RabbitMQ, MinIO, and local equivalents
  of production-only services such as KMS) MUST run locally via Docker Compose with a single
  command and no manual setup beyond documented configuration.
- Deviations from this stack require a documented amendment to this constitution.

## Development Workflow & Quality Gates

- Work follows the Spec Kit flow: specify → clarify → plan → tasks → implement. Scope MUST respect
  the PRD phases (MVP, Phase 2, Phase 3) and its out-of-scope list.
- Every plan MUST include a Constitution Check against Principles I–VII; violations MUST be
  recorded with justification in the plan's complexity tracking.
- Pull requests MUST verify constitution compliance, include tests for critical flows, and pass
  CI before merge.
- Production readiness requires the PRD Definition of Done: idempotent critical flows, retry,
  DLQ, reconciliation, complete audit trail, signed callbacks, hashed artifacts, temporary
  download URLs, vendor-decoupled adapters, observability, runbooks, and completed load, chaos,
  and security testing.

## Governance

This constitution supersedes other practices. `docs/PRD.md` is the product source; where it
conflicts with this constitution, the conflict MUST be resolved by amending one of them, not
ignored.

- Amendments: proposed via pull request that updates this file, states rationale and impact,
  includes a migration plan for affected specs/plans/code, and is approved by the project
  maintainers.
- Versioning: semantic. MAJOR for backward-incompatible removals or redefinitions of principles;
  MINOR for new principles/sections or materially expanded guidance; PATCH for clarifications
  and wording fixes.
- Compliance review: checked at every plan (Constitution Check) and at pull request review;
  unresolved violations block merge.

**Version**: 1.1.0 | **Ratified**: 2026-10-05 | **Last Amended**: 2026-10-06
