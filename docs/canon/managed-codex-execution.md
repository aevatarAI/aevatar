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

`Aevatar.AI.Abstractions` owns the typed target/workspace contracts and
`ICodexExecutionPort`. `Aevatar.AI.ToolProviders.NyxId` owns tool argument
admission and target selection. Application owns managed readiness
orchestration, while Infrastructure owns only external transport:

- `PrivateSshCodexExecutionAdapter` maps `private_ssh` to the typed NyxID SSH executor.
- `ManagedCodexExecutionCoordinator` is the managed-sandbox
  `ICodexExecutionPort`; it reads one committed per-user credential current-state
  read model, evaluates execution readiness, and either fails fast or executes
  exactly once.
- `NyxIdManagedCodexChronoTransport` receives an already-authoritative
  credential descriptor and maps it to the fixed NyxID proxy route for
  `chrono-sandbox`. It does not query credential state or own lifecycle
  orchestration.

The normal managed path is:

```text
codex_exec
  -> Application read one committed credential snapshot
  -> pure execution-readiness assessment
  -> chrono transport
  -> terminal Codex result
```

The workflow run actor remains the authority for step lifecycle and terminal state. A per-user `ManagedCodexCredentialGAgent` separately owns durable, non-secret invocation-credential facts. Its current-state projection is the only query source. No process-local identity or execution registry is introduced.

Managed execution failures retain their typed `CodexExecutionFailureKind` at
the shared tool-receipt boundary. Synthetic receipts and audit artifacts derive
a closed `codex_execution_*` classification from that enum; they never copy the
provider-owned `Code`, `Message`, or `DiagnosticId` into the audit record. The
safe exception class remains `CodexExecutionException`, and `TimedOut` and
`Cancelled` retain their corresponding terminal audit outcomes. Generic thrown
tool exceptions continue to use `tool_execution_exception`. This keeps detector
fingerprints aligned with the established failure domain instead of merging
admission, readiness, transport, response, and execution failures into one
incident class.

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

The temporary internal path uses one constrained NyxID agent key per eligible
NyxID user. It is an invocation credential for NyxID proxy access, not an LLM
provider credential.

Normal `codex_exec` never provisions, reconciles, rotates, or repairs a
credential. It reads one committed credential snapshot and fails fast when the
snapshot is not execution-ready. It does not acquire the mutation lease, bind
or wait for a Projection Session, contact NyxID or Vault for repair, dispatch a
credential Actor command, or retry the chrono request.

Credential mutation is an explicit authenticated operation. The lifecycle API
derives the NyxID subject from the native authority, never from `scope_id`, and
verifies the bearer owner against NyxID `/api/v1/users/me`. Once a ready
descriptor is committed, interactive and background execution resolve the
Vault-backed invocation key without needing a current bearer. Request bodies
and tool arguments cannot nominate another user or provide
credential/provisioning controls.

Native NyxID managed-Codex authority is canonicalized as
`platform=nyxid`, empty tenant, and the exact NyxID user ID. A non-empty tenant
is rejected before credential lookup or mutation because `/api/v1/users/me`
attests only the native user ID; an unattested tenant must never create a second
credential actor or Vault owner scope for the same user.

Eligibility is a typed policy. `Allowlist` admits only configured NyxID user
IDs. `All` admits native NyxID users whose personal `chrono-sandbox` and usable
`chrono-llm-public` UserServices already exist; Aevatar does not create missing
UserServices. Enabling managed Codex also requires the explicit typed
`RolloutBoundary=InternalOnly` startup acknowledgement. No public rollout
boundary is supported while delegation still uses `proxy:*`.

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
these settings during explicit provisioning, reconciliation, and rotation.

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

Aevatar reads the fixed terminal response with `ResponseHeadersRead`, rejects
an oversized `Content-Length`, and stops the response stream as soon as
`MaxResponseBytes` is exceeded. Only then does it parse success, bounded output,
exit code, elapsed milliseconds, and a diagnostic ID. Proxy errors and malformed
chrono responses map to stable typed failures. Raw upstream bodies and
infrastructure exception text are never returned or logged.

The production deadline chain is ordered outside chrono-sandbox's complete
180-second execution lifecycle:

- chrono execution: 180 seconds
- Aevatar managed request: `timeout_secs + ExecutionLifecycleGraceSeconds`,
  normally 300 seconds (`180 + 120`)
- NyxID/ingress non-streaming proxy: at least 315 seconds
- Aevatar NyxID `HttpClient`: 330 seconds
- Workflow canary: at least 360 seconds

`ExecutionLifecycleGraceSeconds` is validated between 120 and 180 seconds. The
transport deadline is linked with caller cancellation, so a shorter caller
deadline still wins. The NyxID client ceiling is only a transport backstop and
must remain above the managed request deadline.

## Credential lifecycle

Normal execution reads one committed credential current-state read model. If
`execution_ready` is false, `codex_exec` returns the corresponding
`execution_readiness_reason`; it does not acquire the mutation lease or call
credential lifecycle dependencies. A proxy authorization denial or
`managed_credential_unavailable` is terminal for that invocation and does not
trigger same-turn repair or retry.

The authenticated lifecycle API is the explicit repair boundary:

- `GET /api/managed-codex/credential`: read projected status
- `POST /api/managed-codex/credential`: idempotently provision or reconcile
- `POST /api/managed-codex/credential/rotate`: force replacement
- `DELETE /api/managed-codex/credential`: revoke

`GET` is read-only and reports `enabled`, `eligible`, lifecycle `status`,
`execution_ready`, `execution_readiness_reason`, authoritative `state_version`,
and pending cleanup count. `status` describes the stored lifecycle state;
`execution_ready` answers whether normal execution may use that exact committed
snapshot. They are deliberately separate: for example, an active descriptor
with an invalid Vault reference has `status=active` and
`execution_ready=false`. The credential owner is resolved from `uid`, `sub`,
`ClaimTypes.NameIdentifier`, or `user_id`; `scope_id` is never treated as a
NyxID subject.

Stable execution-readiness reasons are:

- `ready`: the committed descriptor satisfies every execution invariant
- `managed_target_disabled`: the managed target kill switch is off
- `managed_feature_not_enabled`: the native NyxID user is not eligible
- `managed_credential_not_provisioned`: no committed credential exists
- `managed_credential_inactive`: the committed credential is not active
- `managed_credential_expired`: expiry is missing, malformed, or elapsed
- `managed_credential_owner_invalid`: committed and caller owner identities differ
- `managed_credential_reference_invalid`: the Vault reference contract is invalid
- `managed_credential_service_binding_invalid`: key or UserService binding is invalid

Manual mutation responses are accepted-only receipts. They do not claim that
Actor commit or projection observation has completed. Diagnostic clients may
re-read `GET` to observe the current state, but the normal workflow path does
not poll.

Provision, reconciliation, rotation, and revocation are
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
command deletes nothing. An active reserved NyxID entry without a stable
nonblank key ID cannot form an exact cleanup identity and fails closed before
any provision, reconciliation, or rotation mutation. This validation applies
to every active-key list and relist, including post-issuance persistence
confirmation and policy reconciliation. Issuance compensation may use only the
exact stable nonblank ID returned by that local create or rotate operation; a
blank or enumerated candidate never enters compensation.

Every bearer-authorized Actor-owned cleanup retry and explicit revoke performs
the same read-only identity preflight before deleting a NyxID key, revoking a
Vault locator, or completing an Actor cleanup track. If the check fails after a
replacement credential is already committed, cleanup remains pending and the
committed credential remains ready.

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

The delegation token deliberately lives in the sandbox environment for the run (ADR-0044, #2921). It is single-user and expires in five minutes, but the current `proxy:*` scope is not service-scoped: runner code can use it against other NyxID REST proxy services available to that user during the token lifetime. This is accepted only for eligible, trusted internal users. Broad rollout remains blocked until NyxID either authorizes `llm:proxy` for the fixed `chrono-llm-public` proxy route or enforces a service-specific delegation scope, after which chrono-sandbox and Aevatar must reject `proxy:*`. The formerly planned OpenSandbox Credential Vault substitution is rejected, not deferred: satisfying it forces the weaker-isolation runc runtime.
