---
title: "Platform Audit Trail"
status: accepted
owner: eanzhao
---

# ADR-0039: Platform Audit Trail

## Context

Aevatar needs a platform audit trail for security, governance, and incident
review. The tempting implementation shape is to reuse familiar read-side names
and build an `AuditLogReadModel`, a central audit actor, or an alternate
projection rail that tries to reconstruct facts from endpoint records.

That shape would violate existing architecture rules:

1. Read models are actor-scoped current-state replicas, not append-only audit
   history.
2. Projection already has one canonical committed-fact pipeline.
3. Boundary endpoint records cannot prove committed business facts.
4. Audit storage must never capture credential material, raw identity subjects,
   full prompts, full tool arguments, or full tool results.

The applicable reference frame is security audit logging governance plus
CQRS/event-sourcing: audit records are append-only, allowlist-driven,
sanitized, and separate from business authority.

## Decision

Platform audit trail v1 is an append-only audit artifact store.

Audit capture is split by plane:

1. Boundary endpoint filter captures request-plane artifacts.
2. Tool-execution middleware captures tool-plane artifacts.
3. Projection Pipeline artifact sink captures committed-fact-plane artifacts.

Each plane writes sanitized audit artifacts directly to the audit artifact
store. The Projection Pipeline artifact sink consumes only the existing
committed feed. It is a fan-out materialization target, not a new projection
pipeline, read model, or actor authority.

Identity in audit artifacts uses an HMAC-derived key plus `identity_key_id`.
The HMAC key is host-owned configuration or KMS material. Audit artifacts never
store raw token-minting subject ids, `sender_binding_id`, credential material,
tokens, headers, cookies, OAuth codes, API keys, full prompts, full tool
arguments, or full tool results.

The canonical implementation vocabulary and validation rules live in
[Platform Audit Trail](../canon/audit-trail.md).

## Locked Rules

1. Platform audit trail is an append-only artifact store, not an actor-scoped
   current-state read model.
2. No `AuditLogReadModel` or equivalent readmodel-shaped audit history.
3. No dedicated hot audit actor that becomes a shared audit serialization
   bottleneck.
4. No second projection rail, second event envelope, or parallel reducer route.
5. Committed-fact audit artifacts come only from the committed feed consumed by
   the existing Projection Pipeline.
6. Boundary endpoint artifacts may record request acceptance, rejection, and
   safe receipt metadata, but they must not reconstruct committed business
   facts.
7. Tool-execution artifacts record safe execution summaries and artifact
   references, never full prompts, full arguments, full results, or model
   payloads.
8. Credential material, tokens, headers, cookies, OAuth codes, API keys,
   `sender_binding_id`, raw token-minting subject ids, full prompts, full tool
   args, and full tool results are structurally excluded from artifact schemas.
9. Audit identity uses HMAC plus `identity_key_id`; raw subjects are not stored
   for later joins.
10. Audit queries read audit artifacts only. They must not trigger projection
    activation, event replay, actor state reads, or query-time reconstruction.

## Required Artifact Contract

The first implementation must define a typed audit artifact contract with these
field families:

| Field family | Required meaning |
|---|---|
| `audit_id` | Stable audit artifact id. |
| `captured_at` | Capture timestamp. |
| `capture_plane` | Boundary endpoint, tool execution, or projection artifact. |
| `action` | Allowlisted action name. |
| `resource` | Typed safe resource summary. |
| `actor_identity` | HMAC identity key plus `identity_key_id`. |
| `correlation` | Safe request, command, run, trace, or projection correlation ids. |
| `committed_fact_ref` | Optional committed feed reference and authoritative state version. |
| `outcome` | Allowlisted status or error class. |
| `safe_summary` | Redacted governance summary. |

This ADR does not authorize an untyped payload bag for internal audit
semantics. If an external extension boundary needs a bag, it must remain outside
the internal platform audit contract and obey the same redaction rules.

## Consequences

Positive consequences:

1. Audit trail gets a clear governance surface without becoming a second
   business fact source.
2. CQRS and Projection Pipeline remain single-rail.
3. Endpoint, tool, and committed-fact planes can evolve independently while
   sharing one sanitized artifact store.
4. Identity joins survive key rotation through `identity_key_id` without
   storing raw subjects.
5. Sensitive content is excluded by schema, not by after-the-fact log scrubbing.

Tradeoffs:

1. Audit queries cannot answer current business state questions; callers must
   use read models for that.
2. Some forensic detail is intentionally unavailable because full prompts,
   full tool payloads, headers, cookies, tokens, and credential material are not
   stored.
3. Backfill and export require explicit artifact-store operations rather than
   query-time replay.

## Non-Goals

1. Defining the physical storage provider.
2. Adding a public audit API shape.
3. Adding a new actor, read model, reducer, envelope type, or projection
   lifecycle.
4. Changing existing observability semantic conventions.
5. Reconstructing historical audit detail from raw request bodies, raw tool
   payloads, credential stores, or event-store replay.

## Validation

For this docs-first decision:

```bash
bash tools/docs/lint.sh
dotnet build aevatar.slnx --nologo
```

Future implementation work must additionally prove that forbidden fields cannot
be serialized into audit artifacts and that committed-fact artifacts consume
only committed feed inputs.
