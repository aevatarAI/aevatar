---
title: "Identity OAuth Accepted ACK Semantics"
status: accepted
owner: eanzhao
---

# ADR-0029: Identity OAuth Accepted ACK Semantics

## Context

ADR-0018 defined the per-user NyxID binding model and originally described OAuth callback and `/unbind` completion as write-side projection readiness waits. The current architecture rules require an honest ACK boundary: synchronous HTTP responses may only promise the stage already reached, while `committed` and readmodel-observed guarantees must be obtained through separate observation or query contracts.

Cluster `iter27/cluster-028-identity-oauth-endpoint` removes the endpoint/bootstrap projection waits and makes Identity OAuth write paths dispatch typed CQRS commands through module-local dispatch adapters.

## Decision

Identity OAuth callback, broker revocation, OAuth client bootstrap, and OAuth client rebuild paths return accepted/pending ACKs after typed command dispatch. They do not activate projection scopes, call readiness ports, poll readmodels, or rebuild observations inside the HTTP/background completion path.

The synchronous response only means:

- the request was normalized and validated
- the target actor id was resolved
- the command envelope was accepted for dispatch through the actor dispatch port
- a stable `command_id` / `correlation_id` can be returned to the caller

It does not mean:

- the target actor has committed the command
- the committed event has reached projection
- a readmodel query will immediately observe the new state

Readmodel visibility remains eventually consistent and must be surfaced honestly through existing query/status paths such as `/whoami`, turn gate checks, and `/api/oauth/aevatar-client/status`.

## Superseded ADR-0018 Sections

This ADR supersedes only ADR-0018 sections that required OAuth callback or `/unbind` handlers to synchronously wait for projection readiness.

ADR-0018 remains the source of record for the product model, storage boundary, actor ownership, NyxID broker contract, and zero-secret design.

## Consequences

- `IProjectionReadinessPort`, `ExternalIdentityBindingProjectionReadinessPort`, `ExternalIdentityBindingProjectionPort`, `IExternalIdentityBindingProjectionPort`, `AevatarOAuthClientProjectionPort`, and `AevatarOAuthClientRebuildCoordinator` are removed from the Identity OAuth endpoint/bootstrap completion path.
- Identity OAuth endpoints inject typed `ICommandDispatchService<..., ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>` services instead of directly constructing `EventEnvelope` instances.
- Callback success uses `binding_pending` plus `command_id`, `correlation_id`, and `status_url`.
- Rebuild success uses `rebuild_pending` plus `command_id`, `correlation_id`, and `status_url`.
- Revocation webhook success returns `202 Accepted` once the revoke command is accepted for dispatch.
- Bootstrap dispatches the ensure-provisioned command when the current OAuth client readmodel is missing or drifted, then exits without waiting for readmodel propagation.

## Guardrail

The query/projection priming guard scans Identity OAuth endpoint, bootstrap, and identity tests for the removed readiness/rebuild wait tokens. The matching xUnit source-regression test also reads the endpoint and bootstrap source files and rejects the same tokens so local behavior coverage fails before the shell guard is bypassed.
