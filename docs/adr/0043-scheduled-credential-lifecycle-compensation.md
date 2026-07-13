---
title: "Scheduled Credential Lifecycle Compensation"
status: accepted
owner: eanzhao
supersedes: "ADR-0037 lifecycle implications and ADR-0042 vault-write implications"
---

# ADR-0043: Scheduled Credential Lifecycle Compensation

## Context

Scheduled-agent and delivery-target creation span NyxID key issuance, vault persistence,
and actor initialization. Deletion spans the catalog actor, NyxID, and the vault. These
systems cannot participate in one atomic transaction, so best-effort cleanup in individual
tools can lose work or leave one external resource active.

ADR-0037 and ADR-0042 define the credential source and durable reference. This ADR adds the
lifecycle, compensation, and retry decision without rewriting those historical records.

## Decision

One `ScheduledAgentCredentialLifecycle` owns provisioning and dual-track compensation.
The raw issued key is held by an internal redacted capability that exposes only the vault
store operation. Public DTOs, actor state, projection documents, exceptions, and logs carry
only stable key identifiers and `SecretReference`.

Vault callers may allocate `RequestedRef` before writing. The write is create-only and
idempotent only for the exact same descriptor and secret. Garnet adapters implement this
contract atomically with `SET NX`; every adapter must provide equivalent semantics.

The well-known catalog actor is the sole owner of credential revocation facts. A fact uses
the natural identity `(agent_id, api_key_id, secret_reference.ref)` and independent NyxID
and vault tracks. The actor commits the revocation intent before invoking the external
executor. Bearer tokens are transient command input and are never copied to an event or
state. Failed attempts remain in actor state for idempotent retry; the fact is removed only
after both tracks become terminal.

Command ports return honest accepted-for-dispatch receipts. They do not turn the committed
event stream into synchronous request-reply. The administrator repair endpoint therefore
returns `202 Accepted` with stable request and command identifiers; committed repair or
rejection is observed asynchronously from the catalog read model.

Historical rows without an exact secret reference use
`BLOCKED_MISSING_SECRET_REF` on the vault track. This state is non-terminal, cannot execute
vault revocation, and does not consume attempts. Only the Mainnet administrator repair port
may submit a complete exact descriptor; ordinary tools cannot bypass the block or mark it
not applicable.

## Consequences

- Creation failure, initialization failure, deletion, and future rotation use one durable
  compensation ledger instead of tool-local cleanup paths.
- External effects begin only after the actor has committed the intent in its own turn.
- NyxID retries require transient bearer authority; vault retries do not.
- Repair HTTP responses describe command admission, not committed or read-model-observed
  completion.
