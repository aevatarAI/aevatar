# Managed Codex Through Chrono-Sandbox Design

## Product Decision

`codex_exec` managed execution is a user capability owned by Aevatar, not an
OpenSandbox control-plane feature exposed to workflows. A workflow supplies only
the prompt, fixed `managed_sandbox` target, empty Git workspace, and bounded
timeout. It cannot select an image, command, LLM provider, route, environment,
network policy, or credential.

The temporary internal path uses one constrained NyxID agent key per enabled
NyxID user. The key is an invocation credential for the NyxID proxy and is not an
LLM credential. Aevatar stores its raw value only in `ISecretVault`; actor state,
events, read models, APIs, logs, workflow state, and requests retain only the key
ID, a typed `SecretReference`, exact owner, service ID, status, and expiry.

## Runtime Path

```text
workflow codex_exec
  -> ICodexExecutionPort
  -> read per-user ManagedCodexCredential current-state projection
  -> resolve raw invocation key from ISecretVault just in time
  -> NyxID proxy /s/chrono-sandbox/codex/execute?_nyxid_via=<personal-service-id>
  -> NyxID injects a five-minute proxy:* delegation token for the internal canary
  -> chrono-sandbox owns OpenSandbox and the fixed codex-runner profile
  -> codex-runner calls https://nyx-api.chrono-ai.fun/api/v1/proxy/s/chrono-llm-public
```

The persistent agent key is intended to terminate at NyxID. It is only the
Authorization value on the Aevatar-to-NyxID request and is never serialized into
the chrono request body by Aevatar. NyxID service configuration must keep
`forward_access_token=false`, `inject_delegation_token=true`, and
temporary `delegation_token_scope=proxy:*`.

The widened delegation scope belongs only to the five-minute runner token; the
persistent agent key remains restricted to the exact `chrono-sandbox`
UserService. During that five-minute window, runner code can reach other NyxID
REST proxy services available to the user. The feature must remain allowlisted
and internal until NyxID can enforce a capability limited to
`chrono-llm-public`, after which both Aevatar and chrono-sandbox reject
`proxy:*`.

For the internal P0, that mutable UserService policy is an explicit trust
boundary rather than a guarantee Aevatar can enforce. A UserService owner can
currently change `forward_access_token` after provisioning. Aevatar validates
the policy during provision and rotation, but only NyxID can eliminate the
time-of-check/time-of-use gap. Public rollout therefore remains blocked on
#2899 adding an immutable policy/version or a request-level fail-closed
"never forward the caller credential" constraint.

## Credential Authority

A dedicated `ManagedCodexCredentialGAgent` is keyed by the complete NyxID
authority `(platform, tenant, external_user_id)`. It is not stored in
`UserConfigGAgent`, because user preference scope and NyxID credential ownership
are different identities. It is not stored in a process-local map and it does
not reuse schedule-specific state.

The actor owns:

- the exact NyxID subject;
- active/revoked lifecycle status;
- NyxID API key ID;
- typed Vault reference;
- exact `chrono-sandbox` UserService ID and slug;
- mandatory expiry;
- pending NyxID/Vault revocation facts that need a later authenticated retry.

Execution reads the actor's current-state projection. Command dispatch remains
an accepted-only ACK; no endpoint claims committed or observed state before the
projection catches up.

## Provisioning Boundary

NyxID only permits a personal user to create an agent key with that user's own
bearer. It does not support an admin creating a personal key for an arbitrary
target user. Consequently, the P0 lifecycle endpoints are explicit self-service
actions behind authentication and an internal allowlist. The endpoint derives
the target from both authenticated claims and NyxID `/users/me`; request bodies
cannot nominate another user.

Provisioning resolves the user's exact directly owned active `chrono-sandbox` UserService and
confirms a usable `chrono-llm-public` route. It then creates a key with exactly:

- `scopes="proxy"`;
- `allow_all_services=false`;
- `allowed_service_ids=[<that user's chrono-sandbox UserService ID>]`;
- `allow_all_nodes=false` and `allowed_node_ids=[]`;
- a finite configured expiry.

The response policy is validated before the raw value enters Vault. A wildcard,
missing ID, unexpected service grant, missing raw key, or malformed response is
rejected. If Vault persistence fails after issuance, Aevatar immediately tries
to revoke the NyxID key and persists a non-secret pending revocation fact when
that cleanup cannot complete.

Each issued NyxID key uses a distinct deterministic Vault reference under the
stable per-user vault owner and subject. Rotation never mutates the secret
record referenced by the currently committed actor descriptor. It stores the
new key in a new reference, submits a compare-and-set actor transition, and
retires the previous reference as independent cleanup. This keeps a delayed or
ambiguous actor command from making the committed descriptor point at a newer,
uncommitted secret version.

Provision, rotate, and revoke are serialized per NyxID authority by an
`IManagedCodexCredentialMutationLease`. Production uses a cluster-shared Garnet
lease with owner-token compare-delete and a TTL longer than the bounded mutation
window. Only Development and Testing may use the explicitly named in-memory
implementation. A concurrent mutation fails with a typed conflict; no
process-local registry is a production fact source.

Request cancellation is honored before the first irreversible NyxID/Vault
mutation. Once a mutation starts, a bounded internal completion token carries
the operation to an idempotent recorded or compensating outcome even if the HTTP
caller disconnects. Actor dispatch is accepted-only and can be ambiguous on
failure, so the lifecycle never interprets cancellation/exception as proof of
non-delivery. Deterministic per-key Vault references plus exact active-key reads
allow a later call to redispatch the same descriptor or clean an unadoptable
remote key without rotating an inactive key again.

Revocation attempts the Vault and NyxID tracks independently and always submits
the resulting cleanup facts with the internal completion token. Retrying revoke
is idempotent when either external track already completed. No lifecycle API
returns the raw key.

## Layering

The managed-Codex lifecycle contract, policy, mutation lease port, and business
orchestration live in an Application project. They depend only on typed ports,
`ISecretVault`, and the actor command/query contracts. The ChronoSandbox
Infrastructure project implements the NyxID HTTP adapter, Garnet/in-memory lease
adapters, and chrono execution adapter. Mainnet Host only binds configuration,
selects the environment-appropriate lease adapter, maps HTTP results, and
composes those projects.

## Execution Contract

Aevatar calls the fixed endpoint `POST /codex/execute` through
`NyxIdApiClient.ProxyRequestAsync`, with `_nyxid_via` fixed to the same personal
UserService ID granted to the key. The JSON request contains only:

```json
{
  "prompt": "...",
  "timeout_secs": 180,
  "workspace": "empty_git"
}
```

The terminal JSON response contains `success`, bounded output, exit code,
elapsed milliseconds, and a diagnostic ID. Proxy error envelopes and malformed
or failed chrono responses map to typed, sanitized `CodexExecutionFailure`
values. Raw upstream bodies and exceptions are not returned or logged.

The global `Enabled` option is the kill switch. It blocks new managed execution
and provisioning while leaving status and revocation available. Active actor
state, exact owner/reference validation, Vault resolution, and NyxID proxy
authorization all fail closed independently.

Status derives effective availability from both the committed lifecycle enum
and expiry. An expired committed credential is reported as `expired`, never as
`active`, without mutating actor state from the query path.

## Ownership Boundaries

Aevatar owns workflow semantics, per-user credential lifecycle, Vault storage,
and the NyxID proxy call. NyxID owns key scope enforcement and per-request LLM
delegation. Chrono-sandbox owns OpenSandbox, image pinning, command/profile,
delegation-token validation, resource/egress limits, output bounds, cancellation,
and verified cleanup. Operations deploys and configures those services but never
receives user keys.

## Tests

Tests must prove actor identity separation, protobuf state transitions,
projection mapping, exact agent-key policy and finite remote expiry, Vault
owner/purpose/subject checks, distinct rotation references, compensation,
idempotent reconciliation after ambiguous dispatch, serialized concurrent
mutations, cancellation after each irreversible revoke step, effective expired
status, allowlist and kill-switch admission, fixed chrono request shape, error
mapping, and absence of raw credentials from every serialized result.
Architecture tests must enforce Application ownership of orchestration and
Infrastructure-only NyxID/Garnet adapters. Repository guards must also prove
there is no normal-path `Alibaba.OpenSandbox` dependency or direct OpenSandbox
configuration left.

## Deferred Security Boundary

This remains internal-only security debt. Issue #2899 replaces the persistent
invocation key with a short-lived caller capability and moves the delegated LLM
token behind OpenSandbox Credential Vault without changing workflow arguments.
