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
| `audit_id` | Stable audit artifact id. |
| `captured_at` | Capture timestamp from the writing plane. |
| `capture_plane` | One of `boundary_endpoint`, `tool_execution`, or `projection_artifact`. |
| `action` | Allowlisted action name such as request accepted, tool started, tool completed, or committed artifact observed. |
| `resource` | Typed resource summary, such as scope, service, workflow, run, connector, or channel resource. |
| `actor_identity` | Sanitized identity key and `identity_key_id`, never a raw subject or credential. |
| `correlation` | Request, command, run, trace, or projection correlation ids that are already safe to expose. |
| `committed_fact_ref` | Optional reference to the committed feed item and state version when the artifact is about a committed fact. |
| `outcome` | Allowlisted result summary, error class, or status. |
| `safe_summary` | Short redacted summary suitable for governance review. |

Artifact payloads must be schema-governed and allowlisted. Free-form bags may be
used only at an external extension boundary; internal platform semantics must
use typed fields.

## 3. Capture Planes

### 3.1 Boundary Endpoint Filter

The boundary endpoint filter records request-plane facts at Host or adapter
edges. Examples include authenticated request accepted, authorization denied,
rate limit rejected, request body rejected by validation, and command receipt
returned.

Boundary endpoint artifacts may include safe request method, route template,
scope id, resource id, command id, correlation id, and sanitized caller
identity. They must not reconstruct committed business facts. If the request
later produces a committed event, that committed result is captured by the
committed feed path, not by replaying or enriching the boundary artifact.

### 3.2 Tool-Execution Middleware

Tool-execution middleware records tool-plane facts around tool invocation. It
captures the tool identity, execution phase, safe caller and scope identity,
safe resource target, timing, result class, and redacted diagnostic summary.

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

## 7. Query Semantics

Audit queries read the append-only artifact store. They may filter by time,
capture plane, safe action, safe resource key, sanitized identity key,
`identity_key_id`, correlation id, committed fact reference, and outcome.

Audit query results must expose that they are governance artifacts. They must
not claim current business state, readmodel freshness, or actor completion
beyond the committed fact reference they carry.

If a product surface needs current business state, it must query the relevant
read model. If it needs governance review, it queries audit artifacts.

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

Related references:

- [ADR-0039: Platform Audit Trail](../adr/0039-platform-audit-trail.md)
- [CQRS Projection](cqrs-projection.md)
- [Event Sourcing](event-sourcing.md)
- [Observability](observability.md)
