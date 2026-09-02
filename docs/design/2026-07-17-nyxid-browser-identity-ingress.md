---
title: "NyxID browser identity ingress"
status: active
owner: eanzhao
---

# NyxID browser identity ingress

This document defines the authentication and credential boundary for browser requests that
reach Aevatar through the NyxID proxy. It applies to `nyxid-chat`, `chat-history`, and
`POST /api/chat`.

## Decision

Caller identity and downstream authorization are separate credentials:

| Input | Purpose | Aevatar behavior |
|---|---|---|
| `X-NyxID-Identity-Token` | Authenticate the browser caller | Validate locally and derive the caller scope only from `sub` |
| `X-NyxID-Delegation-Token` | Call NyxID APIs, LLM routes, and tools for the caller | Prefer as the downstream access token; never use it to establish caller identity |
| `Authorization: Bearer ...` | Existing CLI and server-to-server compatibility | Keep as the authentication and downstream credential path when present |

The identity assertion is never forwarded as an access token. Browser traffic therefore
requires NyxID to keep delegation-token injection enabled after access-token forwarding is
disabled.

## Identity assertion contract

Aevatar validates these properties before accepting the assertion:

- Header: `X-NyxID-Identity-Token`
- Algorithm: RS256, with a non-empty `kid`
- Discovery: `https://nyx-api.chrono-ai.fun/.well-known/openid-configuration`
- Issuer: `https://nyx-api.chrono-ai.fun`
- Audience: `urn:aevatar:api`
- Required claims: `sub`, `exp`, `iat`, and `jti`
- Maximum signed lifetime: 60 seconds
- Clock skew: 30 seconds
- Replay handling: `jti` is single-use, with a process-local guard in development and a
  shared Garnet guard outside development/testing

Signature keys are cached and refreshed on an unknown `kid`. Validation fails closed for a
bad signature, issuer, audience, lifetime, required claim, or repeated `jti`.

## Scope enforcement

The signed `sub` claim is the sole scope authority for identity-assertion requests. Aevatar
removes any inbound `scope_id` or `workflow.scope_id` assertion claim and writes one canonical
`scope_id = sub` claim.

- Every `nyxid-chat` and `chat-history` route with a path `scopeId` rejects a mismatch with
  HTTP 403 before a query or command port is called.
- `POST /api/chat` uses the authenticated canonical scope. A body `scopeId` is ignored when it
  differs, so the run is materialized under the caller's own scope.
- A valid Bearer header remains authoritative when both Bearer and identity assertion headers
  are present. This preserves the existing CLI path during rollout.

## NyxID service configuration

> **2026-07-25 internal P0 amendment:** managed Codex transparent readiness
> currently requires the NyxID UserService fronting Aevatar to keep
> `forward_access_token=true`. When both inbound credentials exist, workflow
> ingress selects the forwarded Authorization bearer before the delegation
> token. Do not execute the former rollout step that disables forwarding until
> Aevatar carries both credential purposes as separate typed contracts or NyxID
> provides a delegated self-service readiness capability. The canonical current
> rule is in `docs/canon/nyxid-llm-integration.md`.

The NyxID service that proxies Aevatar must use this effective configuration:

```json
{
  "identity_propagation_mode": "jwt",
  "identity_jwt_audience": "urn:aevatar:api",
  "inject_delegation_token": true,
  "forward_access_token": false
}
```

`inject_delegation_token` is required because `nyxid-chat` and workflow chat can call back into
NyxID after Aevatar authenticates the request. NyxID currently injects a five-minute delegated
access token in `X-NyxID-Delegation-Token` for this purpose.

## Rollout order

1. Configure NyxID to mint the identity assertion for audience `urn:aevatar:api` and inject the
   delegation token, while temporarily retaining access-token forwarding.
2. Deploy Aevatar with identity-assertion validation and scope enforcement.
3. Verify browser calls, Bearer CLI calls, and a cross-scope request that must return HTTP 403.
4. Set `forward_access_token` to `false`. Keep `inject_delegation_token` set to `true`.
