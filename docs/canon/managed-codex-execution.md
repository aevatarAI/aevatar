---
title: "Managed Codex Execution"
status: active
owner: eanzhao
---

# Managed Codex Execution

This document defines the authoritative Aevatar contract for `codex_exec`. The tool has one business entry and two infrastructure targets:

- `private_ssh`: execute a fixed Codex stdin command through a caller-owned NyxID SSH service.
- `managed_sandbox`: ask the user's NyxID `chrono-sandbox` service to run Codex in its managed sandbox runtime.

The targets share parsing, lifecycle events, terminal result semantics, and workflow run authority. They do not share transport, credentials, or isolation configuration.

## Layering

`Aevatar.AI.Abstractions` owns the typed target/workspace contracts and `ICodexExecutionPort`. `Aevatar.AI.ToolProviders.NyxId` owns tool argument admission and target selection. Target adapters own infrastructure behavior:

- `PrivateSshCodexExecutionAdapter` maps `private_ssh` to the typed NyxID SSH executor.
- `ChronoSandboxCodexExecutionAdapter` maps `managed_sandbox` to the fixed NyxID proxy route for `chrono-sandbox`.

The workflow run actor remains the authority for step lifecycle and terminal state. A per-user `ManagedCodexCredentialGAgent` separately owns durable, non-secret invocation-credential facts. Its current-state projection is the only query source. No process-local identity or execution registry is introduced.

## Typed request contract

`CodexExecutionTarget` is a Protobuf `oneof` containing `private_ssh` or `managed_sandbox`. `CodexExecutionWorkspace` is a separate `oneof`; managed execution accepts only `empty_git`, while private SSH accepts no caller-selected workspace.

Mixed payloads fail closed. Managed callers cannot select:

- runner image, architecture, or sandbox implementation
- provider URL, model flags, or credentials
- command, shell fragment, approval policy, or sandbox flags
- arbitrary repository or persistent session
- NyxID proxy slug, route, or headers

The prompt is capped at 6000 UTF-8 bytes by the tool. Aevatar sends it as data in the fixed chrono request and never interpolates it into a local shell command.

## Managed credential boundary

The temporary internal path uses one constrained NyxID agent key per enabled NyxID user. It is an invocation credential for NyxID proxy access, not an LLM provider credential.

Provisioning is authenticated self-service because NyxID permits a user to create personal API keys only for that same user. The endpoint derives the subject from authenticated claims, then the lifecycle service verifies that subject against NyxID `/api/v1/users/me`. Request bodies cannot nominate another user.

The issued key must have exactly:

- scope `proxy`
- `allow_all_services=false`
- `allowed_service_ids` equal, order-independently, to that user's directly
  owned active `chrono-sandbox` UserService ID and usable
  `chrono-llm-public` UserService ID
- `allow_all_nodes=false` and no node grants
- a finite configured expiry

No extra service grant is accepted. NyxID's `chrono-sandbox` UserService must
set `forward_access_token=false`, `inject_delegation_token=true`, and the
temporary internal-canary `delegation_token_scope=proxy:*`. Aevatar validates
these settings during provisioning, rotation, and transparent readiness
repair.

The only persistent raw-key copy is stored in `ISecretVault`. Actor state, events, read models, APIs, logs, workflow state, and chrono request bodies contain only typed non-secret facts such as the key ID and `SecretReference`. Execution resolves the raw value immediately before the NyxID request and uses it only as that request's Authorization value. Aevatar never intentionally serializes or forwards it to chrono-sandbox or codex-runner.

For the internal P0, the NyxID UserService forwarding policy is a trust boundary rather than an end-to-end guarantee Aevatar can enforce. The UserService owner can currently change `forward_access_token` after Aevatar validates it. Broad or public rollout remains blocked on #2899 providing immutable/version-bound policy or a request-level fail-closed guarantee that NyxID will not forward the caller credential.

## Managed runtime call

Aevatar sends exactly one fixed proxy request:

```text
POST /api/v1/proxy/s/chrono-sandbox/codex/execute?_nyxid_via=<chrono-sandbox-user-service-id>
Authorization: Bearer <per-user agent key resolved from ISecretVault>
```

The server-selected `_nyxid_via` value is the same personal UserService ID stored in the credential descriptor and granted to the key. NyxID strips this internal routing parameter before forwarding the request. This prevents slug auto-resolution from selecting an inherited service when the user has multiple services with the same slug.

The JSON body contains only:

```json
{
  "prompt": "...",
  "timeout_secs": 180,
  "workspace": "empty_git"
}
```

The interactive workflow bearer is not used for the chrono request. Under the validated UserService policy, NyxID validates the agent key without forwarding it and injects a five-minute `proxy:*` delegation token for chrono-sandbox. Chrono-sandbox validates that exact token scope before sandbox creation and passes it to the one-shot Codex process only as request-local `NYXID_LLM_TOKEN` through execd's native environment map. Codex uses the fixed `https://nyx-api.chrono-ai.fun/api/v1/proxy/s/chrono-llm-public` Responses base URL. Per ADR-0044 (#2921), direct injection of this short-lived token is the decided credential model: there is no sandbox-side credential vault, no placeholder substitution, and no TLS-intercepting credential proxy. Chrono-sandbox owns OpenSandbox, the immutable runner image, Codex provider configuration, resource limits, output bounds, cancellation, and cleanup.

The managed runtime is a gVisor tenant. The runner executes Codex with its inner sandbox disabled; escape isolation is the gVisor boundary, and there is no fail-closed Landlock preflight. Egress scoping is an IP-level Kubernetes NetworkPolicy owned by operations — coarser than an FQDN allow-list because the NyxID gateway sits behind a shared CDN range — with no egress sidecar. The sandbox create call requests no `networkPolicy` and no `credentialProxy`.

Aevatar parses only the fixed terminal response containing success, bounded output, exit code, elapsed milliseconds, and a diagnostic ID. Proxy errors and malformed chrono responses map to stable typed failures. Raw upstream bodies and infrastructure exception text are never returned or logged.

## Credential lifecycle

The authenticated self-service API is:

- `GET /api/managed-codex/credential`: read projected status
- `POST /api/managed-codex/credential`: provision
- `POST /api/managed-codex/credential/rotate`: rotate
- `DELETE /api/managed-codex/credential`: revoke

Mutation responses are accepted-only receipts. They do not claim that actor commit or projection observation has completed. Clients re-read `GET` to observe the current state.

Provision, rotation, revocation, and transparent readiness repair are
serialized per NyxID authority by a cluster-shared Garnet lease in production.
Development and Testing may use the explicitly scoped in-memory lease. Every
lease holder anchors one absolute deadline immediately before acquisition.
`MutationCompletionSeconds` bounds primary work, while fixed later reserves
cover compensation and durable Actor recording and a final safety margin keeps
all work inside the Garnet TTL. Configuration requires
`MutationLeaseSeconds >= MutationCompletionSeconds + 30`. Caller cancellation
is honored before irreversible mutation; afterward, outcome completion uses
only those lease-bound phase deadlines.

Every issued NyxID key has its own deterministic Vault reference. Rotation
stores the new key at a new reference and submits a compare-and-set Actor
transition carrying typed cleanup for the exact previous API-key ID and Vault
locator. Provision, rotation, and policy reconciliation also carry typed
cleanup intents for every remotely observed obsolete credential. The Actor
validates and commits the incoming descriptor and all cleanup intents
atomically; it rejects an intent that targets the incoming credential and
rejects any incoming or current descriptor already targeted by an active
pending cleanup track. Application never deletes an observed remote key or
Vault locator before that atomic credential commit is observed. After commit,
it retries only the Actor-owned pending tracks and completes each track by the
exact `(ApiKeyId, SecretRef)` identity.

Manual provision/rotation reconciliation follows the same rule. A remotely
listed key that fails validation or lacks its deterministic Vault reference is
carried as obsolete cleanup on the subsequent credential command; a rejected
command deletes nothing.

For one API key with multiple historical Vault locators, exactly one cleanup
fact owns `NyxIdPending`, while every distinct locator may independently own
`VaultPending`. Rotation gives NyxID ownership to the exact previous Actor
credential cleanup; otherwise the Actor chooses the stable sorted locator.
Application therefore revokes NyxID once per key and Vault once per exact
locator. It never overwrites or retires the secret referenced by the active
descriptor before commit. Provision and rotation re-read NyxID to verify the
persisted key's active state, exact grants, platform, and expiry before Vault
persistence. A later lifecycle call reconciles a valid active remote key and
deterministic Vault reference after an ambiguous Actor dispatch instead of
issuing another key blindly. Same-key, same-locator reference drift selects
replacement and completes readiness in the same call rather than certifying
stale reference metadata.

Revocation runs the NyxID and Vault tracks independently. Cleanup of
non-current orphan keys derives each deterministic Vault reference and enters
the complete pending intent set in the same Actor commit as the replacement or
provisioned credential. Caller cancellation during post-commit cleanup cannot
erase those facts; a successful track is completed explicitly and an
interrupted or failed track remains pending. Cleanup-recording admission is
never ignored: if an uncommitted compensation outcome cannot be durably
recorded, the lifecycle returns
`managed_credential_persistence_pending`. Manual revoke likewise catches
compensation-boundary expiry, marks unknown or unattempted tracks pending, and
uses the still-live recording reserve to commit the revoked state. Cancellation,
exception, or rejected admission while recording that post-destruction revoked
state also returns `managed_credential_persistence_pending`. Once a ready
credential is committed, cleanup timeout or rejected track-completion admission
is best effort in both Normal and Force validation modes and does not suppress
readiness. Status derives `expired` from an active descriptor whose committed
expiry has passed, without writing from the query path.

The global `Enabled` option is the kill switch. It blocks managed execution, provisioning, and rotation while leaving status and revocation available.

## Ownership

Aevatar owns workflow semantics, the per-user credential actor/projection, Vault storage, lifecycle endpoints, and the fixed NyxID proxy call. NyxID owns agent-key policy enforcement and delegation-token injection. Chrono-sandbox owns OpenSandbox and the runner execution boundary. Operations owns the gVisor tenant and its egress NetworkPolicy, deploys and configures NyxID/chrono-sandbox, but never receives users' agent keys.

The immutable runner image remains built from `containers/codex-runner`, but it is consumed by chrono-sandbox rather than directly by Aevatar. Production rollout requirements are maintained in `docs/operations/2026-07-16-managed-codex-exec-rollout.md`.

## Deferred security boundary

This internal-only design intentionally uses a persistent per-user invocation key and trusts mutable NyxID forwarding policy. Issue #2899's remaining scope replaces the key with a short-lived caller capability and adds immutable or request-level caller-credential non-forwarding, without changing workflow arguments.

The delegation token deliberately lives in the sandbox environment for the run (ADR-0044, #2921). It is single-user and expires in five minutes, but the current `proxy:*` scope is not service-scoped: runner code can use it against other NyxID REST proxy services available to that user during the token lifetime. This is accepted only for explicitly allowlisted, trusted internal canaries. Broad rollout remains blocked until NyxID either authorizes `llm:proxy` for the fixed `chrono-llm-public` proxy route or enforces a service-specific delegation scope, after which chrono-sandbox and Aevatar must reject `proxy:*`. The formerly planned OpenSandbox Credential Vault substitution is rejected, not deferred: satisfying it forces the weaker-isolation runc runtime.
