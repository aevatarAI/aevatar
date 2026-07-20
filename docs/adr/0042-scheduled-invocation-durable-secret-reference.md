---
title: "Scheduled Invocation Durable Credential Uses SecretReference"
status: accepted
owner: eanzhao
---

# ADR-0042: Scheduled Invocation Durable Credential Uses SecretReference

## Context

ADR-0037 correctly rejected raw durable bearer material in scheduled-dispatch state, HTTP
requests, readmodels, and logs. Its durable reference wording still described an id-to-token
exchange authority that the current NyxID surface does not provide. The implementation
consensus for #2407 tightened that boundary: the durable handle is a vault subject, not a
token minting request.

The repository already has `ISecretVault` and typed `SecretReference` support for long-lived
secrets. Scheduled invocation should reuse that capability instead of inventing another
credential authority.

## Decision

Durable scheduled invocation credential state is:

- `credential_id`, the NyxID agent key handle and vault subject id.
- `SecretReference`, the opaque vault reference for the full key.

`ScheduledServiceInvocationAuthState.durable` is final proto tag `6`; the unified
`nyx_id` credential source remains proto tag `5`.
The old raw `durable_sender_bearer_token` field `2` remains parse-only and fail-closed for
historical events; reducers and runtime dispatch must not copy or use its value.

The usable full key is resolved only at fire time through `ISecretVault` with purpose
`scheduled.nyx-api-key`. HTTP request DTOs, application readmodels, actor state logs, and
projection documents never carry raw bearer material.

Future credential-source fields must not move or reuse `nyx_id` tag `5`, `durable` tag `6`,
or the scheduled invocation agent-key tag `7`; the next new source tag starts at `8`.

## Consequences

- Scheduled-dispatch no longer depends on a NyxID id-to-token exchange capability.
- Public `/api/schedules` continues to reject raw durable bearer input and does not expose
  durable reference creation.
- Trusted internal provisioning may write `credential_id + SecretReference`, and the fire
  adapter late-resolves the full key for the single invocation attempt.
- ADR-0037 remains the broader credential-source model, with this ADR superseding only its
  durable reference wording and locking the tag policy.
