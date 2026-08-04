---
title: "Platform Audit Trail"
status: active
owner: eanzhao
---

# Platform Audit Trail

## 1. Purpose

Platform audit trail is the governance record for security-relevant platform
actions. It answers: who initiated an action, which plane captured it, what
resource was touched, what decision or outcome was recorded, and which
committed fact version proves the business result when a committed fact exists.

It is not a domain read model, not an actor state replica, not an observability
trace, and not a second projection rail.

The mature frame is security audit logging governance plus CQRS/event-sourcing:
audit records are append-only, allowlist-driven, sanitized, and separated from
business authority.

## 2. Canonical Shape

Platform audit trail v1 is an append-only audit artifact store.

Each audit artifact is an immutable record written by exactly one capture
plane. The artifact can be queried for governance and incident review, but it
does not define business state and cannot be used as the source for command
decisions.

Required artifact shape:

| Field family | Meaning |
|---|---|
| `audit_id`, `event_kind`, `subject`, `source` | Stable event identity and producer-owned subject. |
| `occurred_at`, `recorded_at` | Time the fact occurred and time the capture plane recorded it. |
| `schema_version` | Version of the durable audit payload contract. |
| `capture_plane` | One of `boundary_endpoint`, `tool_execution`, or `projection_artifact`. |
| `lifecycle_phase` | When the event kind owns lifecycle semantics: `accepted`, `running`, `waiting_approval`, or `terminal` for the immutable fact's subject. |
| `terminal_outcome` | Exactly one of `succeeded`, `failed`, `cancelled`, or `timed_out` on terminal records; absent otherwise. |
| `failure` | Stable code, category, retryability, failed phase, and sanitized message for failed or timed-out terminal records. |
| `resource` | Typed resource summary, such as scope, service, workflow, run, connector, or channel resource. |
| `actor_identity` | Sanitized identity key and `identity_key_id`, never a raw subject or credential. |
| `correlation` | W3C `traceparent`/`tracestate`, OpenTelemetry `trace_id`/`span_id`, and safe request, command, run, correlation, or causation ids. |
| `provenance` | Applicable scope, team, member, workflow, published-service, run, causation, correlation, and committed actor sequence identities. |
| `committed_fact_ref` | Optional reference to the committed feed item and state version when the artifact is about a committed fact. |
| `redaction` | Policy, omitted field names, and whether retained values were sanitized. |
| `safe_summary` | Short redacted summary suitable for governance review. |

Artifact payloads must be schema-governed and allowlisted. Free-form bags may be
used only at an external extension boundary; internal platform semantics must
use typed fields.

Lifecycle is scoped to the subject of each immutable record, not to a global
product workflow. A boundary `2xx` receipt is `accepted` and nonterminal. It is
never upgraded to execution success by the HTTP capture plane. Terminal command
or run outcomes are emitted only by the committed producer that owns that fact.
Event kinds without producer-owned lifecycle or execution-provenance semantics
leave those fields unspecified rather than inventing identities or outcomes.

## 3. Capture Planes

### 3.1 Boundary Endpoint Capture

Boundary endpoint capture records request-plane facts at Host or adapter edges
for annotated HTTP endpoints. Examples include authenticated request attempted,
authorization denied, rate limit rejected, request body rejected by validation,
and command receipt returned.

Boundary endpoint artifacts may include safe request method, route template,
scope id, resource id, command id, correlation id, and sanitized caller
identity. They must not reconstruct committed business facts. If the request
later produces a committed event, that committed result is captured by the
committed feed path, not by replaying or enriching the boundary artifact.

Endpoint owners declare audit intent with strongly typed endpoint metadata:

| Field | Meaning |
|---|---|
| `operation_name` | Stable allowlisted operation name. |
| `sensitivity_level` | Audit sensitivity of the endpoint surface. |
| `target_kind` | Safe target resource family. |
| `target_resolver` | Safe target id/display resolver from route values or other allowlisted values. |
| `request_sanitizer` / `result_sanitizer` | Allowlist summary builders; they must not copy request bodies, headers, tokens, cookies, prompts, credentials, raw subjects, or other forbidden material. |
| `capture_unauthenticated` | Opt-in for explicitly `AllowAnonymous` ingress surfaces (signature/token trust boundary). When set, unauthenticated attempts are recorded under a fixed anonymous canonical actor key. Defaults to false. |

The endpoint filter runs only for annotated endpoints. It captures safe
request/result summaries and target resolution for handler executions. The
filter does not append audit records and does not define terminal authorization
outcomes.

The boundary capture middleware is registered by `Aevatar.Bootstrap` after
`UseAuthentication()` and before `UseAuthorization()`. That placement lets the
host record authenticated attempts and the terminal outcome even when
authorization middleware short-circuits with `403` before endpoint filters or
handlers run. Unauthenticated `401` challenges are not recorded by default
because there is no authenticated actor to hash.

Anonymous ingress surfaces are the exception. Endpoints whose trust boundary is
a request signature or a stateless token rather than a platform user — OAuth
callbacks, HMAC-signed webhooks, and relay ingress — are explicitly
`AllowAnonymous` yet still perform security-relevant work. Such an endpoint opts
in with `capture_unauthenticated` on its endpoint audit metadata. When set, the
middleware records the attempt and terminal outcome even for an unauthenticated
caller, hashing a fixed anonymous canonical actor key (never a request-supplied
value) and marking the record as a system-captured governance fact. The opt-in
is per endpoint; ordinary endpoints keep the default posture so `401` challenge
floods are not recorded.

The middleware is host glue only:

1. Read endpoint audit metadata from the selected endpoint.
2. Resolve the authenticated caller through `IAuditActorIdentityHasher`.
3. Append `operation_name.attempted` plus exactly one boundary-result
   `operation_name` record through `IAuditTrailAppender`. A `2xx` result remains
   nonterminal `accepted`; a boundary rejection, error, cancellation, or timeout
   is terminal only for the HTTP request subject.
4. Fail open for business responses if audit append fails, while logging the
   operational failure.

Bootstrap must not define audit record schema, storage, query, retention,
identity hashing implementation, business endpoint inventory, or concrete
business sanitizer catalogs. Those remain in audit contracts, audit core, or
endpoint-owning modules.

Allowed endpoint summaries are intentionally narrow: route template, safe route
ids, status/outcome class, trace/request correlation, and sanitized target
identity. Token-shaped or secret-key-shaped values are redacted before append.

### 3.2 Admitted Tool Execution

Tool-plane audit is part of the canonical admitted execution boundary, not an
optional middleware around individual callers. Every server-owned `IAgentTool`
call enters `IAgentToolExecutionPort`; only `AdmittedAgentToolExecutor` may call
the raw `IAgentTool.ExecuteAsync` terminal.

The executor freezes the final argument string after caller-owned hooks, derives
its SHA-256 digest, and calls `GetCallSafety` once. Credential policy,
actor-owned approval, audit artifacts, receipt construction, and terminal
execution all use that exact payload and classification.

Execution admission and audit observation have separate fact owners.
`IAgentToolAdmissionLedger` is the authoritative start-once ledger. It atomically
creates a strongly typed `AgentToolAdmissionFact` containing the authoritative typed
execution owner plus the stable admission, request, call, tool, and argument-digest
identities and the request's immutable issued time. The owner is supplied at the actor, workflow-run, channel-registration,
connector, or host-service boundary; request and call ids remain correlation identities
and never stand in for ownership. Only `Started` permits entry to the raw terminal.
`Duplicate` and `Conflict` fail closed without replay;
invalid or expired replay lifetimes also fail closed without invoking the terminal, while
`StoreUnavailable` fails closed before invocation and may be retried. Every host that enables
server-owned tools must replace the unavailable default ledger. Development and Testing may use
the in-memory implementation; other environments require a durable compare-and-set implementation.

Each enabled host owns its tool-admission retention and key namespace through
`AgentToolAdmission:MaximumRequestLifetime`,
`AgentToolAdmission:MaximumFutureClockSkew`, and `AgentToolAdmission:KeyPrefix`. Mainnet defaults
to `aevatar:mainnet:agent-tool-admission:v1:`; Workflow defaults to
`aevatar:workflow:agent-tool-admission:v1:` and requires
`AgentToolAdmission:RedisConnectionString` outside Development and Testing. The lifetime defaults
are 24 hours and 5 minutes, and the request lifetime is capped at 30 days. The request-issued time
is a typed protobuf field preserved across actor continuation. The ledger validates that immutable
time before admission and gives each distributed compare-and-set key a TTL equal to the remaining
legal replay window. Redis/Garnet expiration is the cleanup mechanism. Deleting the key does not
renew authority: replaying the original fact after its deadline is rejected as expired before
another atomic insert can occur.

Audit append status never grants execution. The audit phases are observational:

| Phase | Meaning | Audit rule |
|---|---|---|
| `WAITING_APPROVAL` | The owning actor must persist and later reconcile an exact pending call. | Append the sanitized waiting fact. Append failure changes only `AuditCompleted`; actor-owned approval state remains authoritative. |
| `RUNNING` | The admission ledger already accepted the exact call for start-once execution. | Append the sanitized start observation before invoking the raw terminal. `Appended`, `Duplicate`, `Conflict`, or store availability never changes the ledger decision. |
| `TERMINAL` | The actual executed, denied, or failed outcome. | `Appended` or same-fact `Duplicate` completes audit. Failure after terminal invocation preserves the actual result and never makes the tool retryable. |

A required approval is valid only in `ActorOwned` continuation mode and only
when its durable grant matches `ExecutionOwner`, `ApprovalRequestId`, `RequestId`,
`ToolName`, `ToolCallId`, and `ArgumentsSha256`. Approval, admission, running-audit,
and terminal-audit ids all include the owner kind and owner id, so identical correlation
ids in two owner namespaces cannot collide. Credential policy runs before grant
validation. An unavailable admission ledger is retryable because the terminal
was not invoked; a successful execution whose running or terminal audit is
incomplete returns `ExecutedAuditIncomplete` with its real result and
`Retryable=false`.

Tool audit captures the tool identity, execution phase, lifecycle phase,
argument digest, safe
caller and scope identity, safe resource target, credential source, timing,
result class, and redacted diagnostic summary. Business lifecycle uses the typed
`AuditLifecyclePhase`; the observation phase, argument digest, and mutation
semantics use the typed `AuditToolExecution` submessage and
`AuditToolExecutionPhase`. These platform semantics must not be written
to `Annotations`. It must not store full prompts,
full tool arguments, full tool results, raw model responses, bearer tokens,
OAuth codes, API keys, cookies, headers, or connector credential material. If a
tool result needs later inspection, the tool must produce a separate safe
artifact reference and record only that reference in the audit artifact.

Client-forwarded tools are outside the local terminal boundary. They are
recorded as forwarded continuation state and do not enter
`IAgentToolExecutionPort`.

`AgentToolReceipt` is the single execution-outcome fact shared by audit,
workflow artifacts, streaming results, and user-visible completion. A tool that
returns without throwing but does not provide a receipt has an unverified
outcome: the admitted executor records an `Unspecified` receipt, audit keeps the
business outcome running and nonterminal, and consumers must not upgrade it to
success. Providers must emit typed receipts for outcomes they can classify,
including HTTP/authentication failures, DNS or TLS failures, and timeouts. Only
an explicit `Success` receipt confirms completed execution; approval-required
receipts remain waiting and `Unspecified` receipts remain unknown.

Provider receipts must use the same stable resource target for successful and
failed calls. Invocation ids are correlation identifiers only and must not
stand in for the downstream resource. NyxID proxy receipts identify an admitted
exact UserService as `nyxid.user-service/<user_service_id>`; they do not derive
identity from service slugs, request paths, call ids, or response content.
When outcome classification depends on execution-only facts such as HTTP status
or completed file ingress, the provider returns that receipt with the execution
result. Post-result `CreateResultReceipt` remains the compatibility path for
tools whose result and arguments retain sufficient evidence. Both paths pass
through the same canonical receipt finalizer before audit, workflow, or
streaming consumption; response JSON shape alone never proves success or
failure.

NyxID proxy failure classification is limited to exact
`NYXID_PROXY_UNAUTHORIZED`, exact `NYXID_PROXY_FORBIDDEN`, and the full-value
form `NYXID_PROXY_HTTP_[1-5][0-9][0-9]`. Other provider strings remain the
generic `tool_error` classification. Raw messages, arguments, results, paths,
headers, and credentials remain excluded from audit artifacts.

It must not store full prompts, full tool arguments, full tool results, raw
model responses, bearer tokens, OAuth codes, API keys, cookies, headers, or
connector credential material. If a tool result needs later inspection, the
tool must produce a separate safe artifact reference and record only that
reference in the audit artifact.

### 3.3 Projection Pipeline Artifact Sink

The Projection Pipeline artifact sink records committed-fact-plane audit
artifacts after committed facts flow through the existing Projection Pipeline.
This plane consumes the same committed feed as the normal projection path and
writes audit artifacts as append-only governance artifacts.

It must not create a dedicated audit read model such as `AuditLogReadModel`,
must not add a hot audit actor, and must not subscribe to inbound commands,
self-continuation events, actor runtime structures, or boundary-only records to
infer committed facts.

Committed fact artifacts must reference the authoritative source version, such
as committed state event id, actor id, actor type, event type url, and state
version when available. Local artifact counters are not authoritative state
versions.

## 4. Identity and Redaction

Audit identity is represented by an HMAC-derived key plus `identity_key_id`.

Rules:

1. The HMAC secret is host-owned configuration or KMS material. It is never
   written to actor state, read models, audit artifacts, logs, traces, or
   repository defaults.
2. `identity_key_id` identifies the active key used for the digest so rotation
   can be verified later.
3. Raw platform subjects, token-minting subject ids, `sender_binding_id`,
   OAuth subject ids, email addresses, phone numbers, access tokens, refresh
   tokens, API keys, cookies, authorization headers, and full credential
   handles are structurally excluded from audit artifacts.
4. Rotation keeps old `identity_key_id` values queryable for historical
   artifacts. Rewriting old artifacts is not required.
5. Joins across planes use the sanitized identity key plus safe correlation ids,
   not raw subjects.

The exclusion rule is structural, not best-effort logging hygiene. Producers
must shape the artifact contract so forbidden material has no field where it
can be stored.

## 5. Relationship to CQRS, Projection, and Observability

### 5.1 CQRS Boundary

Audit artifacts are not command receipts, domain events, actor state, or current
state read models.

Commands still follow:

```text
Command -> Actor -> Domain Event -> Committed Feed -> Projection -> ReadModel
```

Audit trail follows:

```text
Capture Plane -> Sanitized Audit Artifact -> Append-Only Artifact Store
```

When an audit artifact references a committed business fact, it references the
committed feed item. It does not become the fact.

### 5.2 Projection Boundary

The Projection Pipeline may fan out to an audit artifact sink. That sink is a
materialization target for governance artifacts, not a new Projection Pipeline
and not a second event router.

Projection must continue to consume committed facts through the canonical
`EventEnvelope<CommittedStateEventPublished>` path. Audit capture must not
justify query-time replay, projection priming, event-store side reads, or
boundary-only reconstruction.

### 5.3 Observability Boundary

Observability traces and logs diagnose runtime behavior. Audit artifacts record
governance facts. They can share safe correlation ids, but neither one is the
other's authority.

Observability data may be sampled or aggregated. Audit artifacts are append-only
and retention-governed.

W3C Trace Context and OpenTelemetry identifiers are optional correlation data.
Their absence never invalidates a durable audit fact, and sampled telemetry is
never used to reconstruct the audit trail.

## 6. Forbidden Patterns

Do not implement platform audit trail as:

1. `AuditLogReadModel` or another actor-scoped current-state read model.
2. A dedicated hot audit actor that serializes all platform audit writes.
3. A boundary-only reconstruction of committed facts.
4. A second projection rail, second event envelope, or parallel reducer route.
5. A query-time event replay or readmodel priming path.
6. A raw log sink that accepts untyped payloads, prompts, tool arguments,
   headers, cookies, tokens, OAuth codes, API keys, credential material,
   `sender_binding_id`, raw token-minting subject ids, or full tool results.
7. A governance decision source for command execution.
8. Per-caller tool middleware, wrappers, receipt finalizers, or optional audit
   wiring that can reach a raw `IAgentTool` terminal.
9. Replaying a tool after the admission ledger returned `Duplicate` or
   `Conflict`, or after the raw terminal ran but terminal audit append failed.

## 7. Query Semantics

Audit queries read the append-only artifact store. They may filter by time,
capture plane, safe action, safe resource key, sanitized identity key,
`identity_key_id`, correlation id, committed fact reference, and outcome.

Audit query results must expose that they are governance artifacts. They must
not claim current business state, readmodel freshness, or actor completion
beyond the committed fact reference they carry.

If a product surface needs current business state, it must query the relevant
read model. If it needs governance review, it queries audit artifacts.

### 7.1 HTTP Query Capability

`Aevatar.Audit.Hosting` owns the audit read surface. Host projects may compose it as
a capability bundle, but endpoint handlers must not read projection stores, document
readers, actor state, or event stores directly. Audit artifacts are queried through
`IAuditTrailQueryPort`.

| Route | Method | Authorization | Semantics |
|---|---|---|---|
| `/api/audit/trail` | `GET` | Authenticated caller; platform admin only when `scope` targets another scope | Query materialized audit artifacts. Missing `scope` means caller scope. |
| `/api/audit/trail/cloudevents` | `GET` | Same as `/api/audit/trail` | Export the selected page as a CloudEvents 1.0 JSON batch. |
| `/api/audit/actor-resolutions` | `POST` | Platform admin | Resolve an external actor identity to `auditActorId`. |
| `/api/audit/chat-activity` | `GET` | Authenticated caller; platform admin only for explicit `scope=__all__` | Query only typed chat tool/action artifacts. The default remains the caller's own scope and HMAC identities. |

The resolver accepts raw external identity only in the JSON request body. It must never
put that identity in path or query parameters, must not log it, and must not return it.
The only returned identity is the server-computed `auditActorId` plus
`identityKeyId` from `IAuditActorIdentityHasher`.

Default audit queries resolve to the caller's `scope_id` claim and do not call the
platform-admin authorizer. Any cross-scope query must resolve the caller through
`IPlatformAdminAuthorizer` before `IAuditTrailQueryPort` is invoked. Resolver calls are
always platform-admin reads.

If `IAuditTrailQueryPort` is not configured, `/api/audit/trail` returns
`503 AUDIT_QUERY_UNAVAILABLE`; it must not fall back to projection store access, actor
state reads, query-time replay, or event-store reconstruction. If platform-admin
authorization is unavailable for an admin-only path, the endpoint returns
`503 AUDIT_ADMIN_AUTH_UNAVAILABLE`.

`/api/audit/chat-activity` resolves one unambiguous authenticated scope and subject, derives
every current/retained HMAC actor identity, requires typed `provenance.chat`, and applies
surface/conversation/outcome filters before cursor pagination. Ordinary callers cannot select
`scope`, `auditActorId`, or `identityKeyId`. Platform admins remain personal by default and
enter a cross-user query only by explicitly requesting `scope=__all__`. The response never
enriches from chat history, transcript, actor state, or an event replay.

If the configured query store rejects or cannot execute the query, the endpoint returns
`503 AUDIT_QUERY_UNAVAILABLE` with a stable generic message. The response and server log may
identify only the operation, status class, and exception type; they must not include an
Elasticsearch URL, username, password, bearer, request payload, or raw backend exception body.

Audit query responses expose requested and effective windows, continuation cursor,
truncation, ingestion watermark, optional complete-through checkpoint, window
completeness, schema compatibility, and read timestamp. Each record exposes all safe
typed fields, including `occurredAtUtc`, `recordedAtUtc`, lifecycle, failure,
provenance, correlation, redaction, and committed-fact reference.

Audit queries return the newest matching artifacts first, ordered by
`occurredAtUtc DESC` with `auditId ASC` as the deterministic tie-breaker. A
`continuationCursor` continues toward older artifacts. `truncated` is true only when
another record exists. `ingestionWatermark` is the greatest durable `recorded_at`
known to the artifact store across its ingestion stream; it is not derived from the
maximum business occurrence time. `completeThrough` may be set only from a real source
checkpoint that proves ingestion completeness. Without that checkpoint, a bounded
window is honestly `unknown` or `behind_ingestion_watermark`, never guessed complete.
`recorded_at` is the first successful capture time and is excluded from the semantic
content hash, so redelivery with a later capture clock remains idempotent and preserves
the first durable value.

Admin-only resolver reads and cross-scope audit trail reads carry endpoint metadata with
`AccessLevel = ADMIN`. That metadata is for the host self-audit pipeline; it does not
replace the runtime admin gate.

### 7.2 CloudEvents 1.0 Export

CloudEvents is an HTTP/export representation, not an internal envelope. The export
uses `application/cloudevents-batch+json` and maps durable fields as follows:

| CloudEvents attribute | Audit source |
|---|---|
| `specversion` | Literal `1.0`. |
| `id` | Stable `audit_id`; retries and repeated exports do not mint a new id. |
| `source` | Stored producer `source` URI. |
| `type` | Stored `event_kind`. |
| `subject` | Stored audit `subject`. |
| `time` | `occurred_at`. |
| `dataschema` | URI for the stored `schema_version`. |
| `data` | The same sanitized typed record returned by the normal query API. |

Optional extension attributes are `traceparent`, `tracestate`, `correlationid`, and
`causationid`. Export coverage is returned in `Aevatar-Audit-*` response headers.
CloudEvents does not replace `EventEnvelope`, create a projection rail, or make audit
artifacts authoritative business state.

### 7.3 Stored-Record Compatibility

Records written before explicit schema versioning remain readable. The query adapter
marks them `legacy_mapped`, reports `schemaVersion = legacy-v0`, derives only the
unambiguous lifecycle mapping (`Accepted` remains nonterminal; the old terminal status
maps to one terminal outcome), and never writes that projection back to storage. A
page containing such records reports `contains_legacy_records`. Unknown nonempty
schema versions report `incompatible`; they are not silently claimed current.

### 7.4 Elasticsearch Index Lifecycle and Readiness

The Elasticsearch audit artifact store is not a CQRS current-state read model. It is registered
as `IAuditTrailArtifactStore`, `IAuditTrailQueryPort`, and an index lifecycle reconcile target,
but never as `IProjectionReadModelDescriptor`; `/api/cqrs/readmodels` must not inventory it.

Query-critical fields have explicit mappings. `artifact.occurred_at` and
`artifact.recorded_at` are dates; stable string filters and enum values expose explicit
`.keyword` subfields; `id.keyword` is the deterministic cursor tie-breaker.
`artifact.schema_version` is a root keyword because compatibility aggregations operate on that
field directly. Dynamic mapping remains available only for non-query extension fields and cannot
define the types used by the stable query contract.

The governed target is the stable `<prefix>-audit-trail-current` alias backed by a physical
`<alias>-v<schema-fingerprint>` index. Startup reconcile always provisions this target, including
when request-path `AutoCreateIndex=false` and `MissingIndexBehavior=Throw`. This is an explicit
startup lifecycle operation, not Elasticsearch dynamic auto-create and not query-time priming.

For the pre-alias `<prefix>-audit-trail` index, startup reconcile creates the fingerprinted
physical index, reindexes with `op_type=create`, validates completion, and only then attaches the
new alias. The legacy index is retained unchanged. Later schema drift follows the same copy-forward
rule and atomically repoints the alias while retaining the old physical. Reconcile never issues
`DELETE`, `_delete_by_query`, or `remove_index`; retention remains the only deletion authority.

Readiness is active rather than registration-only. `/health/ready` runs a bounded future-window
query through `IAuditTrailQueryPort`. The status dashboard also declares an
`audit-query-index` target whose `audit_query_index` executor records the same availability as
actor-owned health state for `/api/status`. Neither health read endpoint repairs or primes the
index in its query call stack.

## 8. Retention and Operations

Retention, export, and legal hold are artifact-store concerns. They do not
change actor state, committed events, or read models.

Operational requirements:

1. Append-only writes are idempotent by `audit_id` or by a deterministic capture
   key owned by the writing plane.
2. Failed audit writes must be observable as operational failures, but they must
   not be patched by replaying request bodies or reading raw credential stores.
3. Backfill is a maintenance action over safe committed feeds or existing safe
   artifacts. It is not part of query handling.
4. Export jobs must keep the same redaction rules as online queries.
5. Index lifecycle migration copies forward and retains legacy/previous physical indices;
   operators may remove them only through an approved retention action after independent backup
   and count verification.
6. Chat Activity uses a scoped 30-day operation over the active alias: both
   `artifact.recorded_at < now-30d/d` and existence of
   `artifact.record.provenance.chat.surface` are required. This TTL cannot delete unrelated
   Audit Trail records. Retained HMAC identity keys must remain queryable for the full unexpired
   window.

The governed procedure is
[Chat Activity Audit Retention](../operations/chat-activity-audit-retention.md). It defaults to a
count-only dry run; execution and previous-physical-index cleanup require separate approval.
7. Agent-tool admission keys are a separate authorization ledger, not audit artifacts. Every
   enabled host owns its bounded replay-window configuration and distinct key prefix; Redis/Garnet
   TTL expiration performs compaction only after the immutable request deadline has elapsed.

## 9. Validation

Changes to audit trail contracts or implementation must verify:

1. Docs lint passes for this canon and its ADR.
2. Producers cannot store forbidden material because the artifact schema has no
   such fields.
3. Committed-fact audit artifacts consume only committed feed inputs.
4. Audit queries do not trigger projection activation, event replay, actor
   state reads, or request-body reconstruction.
5. Identity key rotation preserves `identity_key_id` and never exposes raw
   subjects.
6. HTTP query endpoints authorize cross-scope reads and resolver calls before
   invoking `IAuditTrailQueryPort` or `IAuditActorIdentityHasher`.
7. Solution-graph checks find exactly one raw `IAgentTool.ExecuteAsync` caller,
   and every known server-owned execution surface invokes
   `IAgentToolExecutionPort`.
8. Credential or approval denial, admission-ledger duplicate/conflict, and
   invalid closed NyxID actions produce zero downstream side effects.
9. Terminal audit failure preserves the actual terminal result and cannot make
   the tool call retryable.
10. Two distinct execution owners using identical request, call, tool, and argument
    values produce distinct admission and audit identities.
11. Real Redis/Garnet adapter tests prove binary round-trip, atomic duplicate/conflict behavior,
    cancellation, bounded TTL, and rejection of a stale fact after retention cleanup.

Related references:

- [ADR-0039: Platform Audit Trail](../adr/0039-platform-audit-trail.md)
- [ADR-0046: Admitted Agent Tool Execution](../adr/0046-admitted-agent-tool-execution.md)
- [CQRS Projection](cqrs-projection.md)
- [Event Sourcing](event-sourcing.md)
- [Observability](observability.md)
