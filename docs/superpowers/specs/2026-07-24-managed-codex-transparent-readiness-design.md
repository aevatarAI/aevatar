# Transparent Managed Codex Readiness Design

## Product Decision

An eligible user invokes `codex_exec` once. Aevatar prepares or repairs that
user's managed Codex credential inside the same application use case and
continues the original execution after the credential actor's committed state
has been observed. The user does not call a provisioning endpoint, inspect a
credential status, manage an agent key, or retry merely because provisioning
was required.

The current product mismatch is:

> The runtime requires users to explicitly manage an infrastructure
> credential, while the product capability is supposed to be immediately
> usable by every eligible user.

This is a runtime, contract, and ownership mismatch. Credential readiness is an
Aevatar-owned application concern, not a workflow argument and not a hidden
Infrastructure-client fallback.

Eligibility and platform readiness remain separate:

- eligibility determines whether Aevatar may create or use a managed Codex
  credential for a NyxID user;
- platform readiness requires that the user already have one usable personal
  `chrono-sandbox` UserService and one usable `chrono-llm-public` UserService.

Without a new NyxID service-registration contract, Aevatar does not fabricate a
missing UserService. Consequently, `All` means all native NyxID users whose
required UserServices are already available. Agent-key creation and repair are
fully transparent; missing UserService registration remains a typed platform
readiness failure.

## Approaches Considered

### Application-owned ensure and execute

An Application coordinator owns one `Ensure credential -> Execute Codex` use
case. Credential mutation is serialized per user, committed through the
credential actor, and observed through the Projection Pipeline before the
original execution continues.

This is the selected approach. It preserves layering, Actor authority,
read/write separation, and same-call completion.

### Infrastructure fallback after a missing-credential error

The chrono client could catch `managed_credential_not_provisioned`, invoke the
lifecycle service, poll the read model, and retry.

This is rejected because it moves business orchestration into Infrastructure,
turns a read-side client into a mutation owner, creates a projection race, and
encourages query-time polling.

### Login-time or background pre-provisioning

Aevatar could try to create keys when users sign in or when an allowlist is
deployed.

This is rejected as the authoritative path because provisioning needs the
user's current NyxID bearer, login is not the owner of managed Codex semantics,
and background provisioning cannot cover every first workflow invocation.

## Configuration Contract

Managed Codex uses a strongly typed eligibility policy:

```json
{
  "Aevatar": {
    "CodexExecution": {
      "ManagedSandbox": {
        "Enabled": true,
        "RolloutBoundary": "InternalOnly",
        "Eligibility": {
          "Mode": "Allowlist",
          "AllowedNyxIdUserIds": [
            "user-id"
          ]
        },
        "CredentialLifetimeDays": 30,
        "MaxResponseBytes": 1048576,
        "MutationLeaseSeconds": 300,
        "MutationCompletionSeconds": 240
      }
    }
  }
}
```

`Mode` has exactly two values:

- `Allowlist`: `AllowedNyxIdUserIds` must contain normalized, distinct user IDs.
- `All`: `AllowedNyxIdUserIds` must be empty.

`RolloutBoundary` has one supported enabled value, `InternalOnly`. Startup
rejects enabled managed Codex when the boundary is unspecified; no public
boundary is supported while the delegated scope remains `proxy:*`.

The default is `Allowlist`. The existing
`ProvisioningAllowedNyxIdUserIds` name is removed because eligibility no longer
means permission to call a separate provisioning workflow. `Enabled` remains
the global kill switch and controls both managed-target discovery and
execution.

## Architecture

```text
NyxIdCodexExecTool
  -> ManagedCodexExecutionCoordinator (Application, ICodexExecutionPort)
       -> ManagedCodexEligibilityPolicy
       -> IManagedCodexCredentialLifecycle.EnsureReadyAsync
            -> IManagedCodexCredentialQueryPort
            -> IManagedCodexCredentialReadinessObservationPort
            -> IManagedCodexCredentialMutationLease
            -> IManagedCodexNyxIdCredentialPort
            -> ISecretVault
            -> IManagedCodexCredentialCommandPort
       -> IManagedCodexChronoTransport
            -> ISecretVault
            -> NyxID proxy
            -> chrono-sandbox
```

`ManagedCodexExecutionCoordinator` lives in
`Aevatar.AI.Application.CodexExecution` and implements the managed-sandbox
`ICodexExecutionPort`. It owns eligibility, credential readiness, one bounded
authorization-repair retry, and terminal event mapping.

The native NyxID authority accepted by the coordinator and lifecycle has an
empty tenant. The NyxID bearer owner check attests the user ID only, so a
non-empty unattested tenant is rejected before deriving the per-user actor or
Vault scope.

`IManagedCodexChronoTransport` is a narrow Application-owned port. Its
Infrastructure implementation receives an already committed credential
descriptor, resolves the referenced secret just in time, and performs the fixed
NyxID proxy request. It does not query credential state, provision keys, select
eligibility, or retry lifecycle mutations. The NyxID client reads this response
with `ResponseHeadersRead`, rejects oversized `Content-Length`, and stops after
`MaxResponseBytes` before materializing the terminal JSON string.

The Host only binds configuration and composes the Application coordinator with
Identity and Infrastructure implementations.

## Typed Credential Contract

`ManagedCodexCredentialDescriptor` gains a typed
`chrono_llm_user_service_id` field. A ready descriptor therefore records:

- exact NyxID owner;
- NyxID API-key ID;
- typed Vault reference;
- exact personal `chrono-sandbox` UserService ID;
- exact `chrono-llm-public` UserService ID;
- fixed `chrono-sandbox` service slug;
- active status and finite expiry.

The LLM UserService ID is not placed in headers, annotations, items, or a
generic bag. It affects authorization and stable execution decisions, so it is
part of the protobuf state, domain events, and current-state read model.

The actor also gains a policy-reconciliation command and event. They update the
two-service authorization fact while preserving the same API-key ID and Vault
reference when NyxID updates the existing key in place. Provision, rotation,
revocation, and policy reconciliation remain separate typed transitions.

Rotation also carries typed `previous_credential_cleanup`. The Actor validates
that its API-key ID and Vault locator exactly match the authoritative prior
descriptor, derives the only valid independent pending-track flags, and commits
the replacement descriptor plus cleanup fact in one event. Generic cleanup
commands cannot target the active API key or active Vault locator. Consequently,
manual rotation and ambiguous-dispatch reconciliation remain accepted-only APIs
without losing durable ownership of the previous credential's retirement.

The read side gains a protobuf readiness snapshot carrying the committed
descriptor, pending cleanup facts, authoritative state version, committed
event ID, and typed readiness evidence. `CurrentStateConfirmed` means the
expected active credential is still the committed structural state;
`RemoteValidated` means an Application readiness path completed fresh
NyxID/Vault validation. A Projection Session publishes this snapshot from
`CommittedStateEventPublished`; it does not infer readiness from inbound
commands or local actor runtime state.

Normal mode accepts either evidence value. `ForceRemoteValidation` accepts only
`RemoteValidated`; `CurrentStateConfirmed` is a coordination signal, not proof
of remote validity. Duplicate provision, rotation, or policy-reconciliation
Actor commands therefore emit only `CurrentStateConfirmed`. The narrow explicit
readiness-confirmation command carries `RemoteValidated` after Application-side
validation. It correlates against the complete expected credential descriptor,
including the exact typed Vault reference, rather than only the API-key ID. The
Actor commits confirmation only when that descriptor equals its current
authoritative credential.

## Credential Policy

Every persistent managed Codex agent key must have exactly:

- `scopes=proxy`;
- `platform=codex`;
- `allow_all_services=false`;
- `allowed_service_ids` equal, order-independently, to the user's exact
  `chrono-sandbox` and `chrono-llm-public` UserService IDs;
- `allow_all_nodes=false`;
- no allowed node IDs;
- a finite configured expiry.

No extra service grant is accepted. The only persistent raw-key copy remains in
`ISecretVault`; lifecycle and transport methods hold it only for the bounded
NyxID operation that needs it. It is the Authorization credential on the
Aevatar-to-NyxID request and is never placed in the chrono body, actor state,
events, read models, workflow state, logs, or results.

NyxID may continue injecting the short-lived delegation token used by the
runner. This design does not put the persistent agent key in chrono-sandbox or
codex-runner and adds no NyxID implementation or configuration change beyond
the already-required usable UserServices.

## Same-Call Readiness Flow

`EnsureReadyAsync` is a write-capable Application use case, not a query API.

1. Validate the native NyxID authority and the configured eligibility policy.
2. Read the credential current-state projection.
3. If the descriptor is complete, active, unexpired, and structurally valid,
   return it without requiring a caller bearer. This supports scheduled or
   background workflows after initial provisioning.
4. Otherwise bind an owner-scoped readiness Projection Session and re-read the
   projection. The re-read closes the gap between the first read and live
   subscription without polling.
5. If the second read is ready, return it.
6. Attempt the per-user distributed mutation lease before requiring a bearer.
7. If the lease is acquired, re-read the authoritative projection, then require
   the current user's NyxID bearer and verify that `/users/me` matches the typed
   caller authority before remote work.
8. If another invocation owns the lease, observe committed readiness evidence
   instead of returning `managed_credential_mutation_in_progress`.
9. A sufficient committed snapshot completes the wait. For a Force caller,
   `CurrentStateConfirmed` is insufficient but triggers at most one distributed
   lease reacquisition attempt. If the lease remains busy, the caller continues
   waiting for `RemoteValidated` evidence regardless of bearer availability. If
   the lease is acquired, a bearer-less caller disposes it and fails with
   `managed_user_authorization_unavailable`; a bearer-equipped caller carries
   the triggering committed snapshot through reacquisition, re-reads the
   projection once, and continues from whichever committed snapshot has the
   higher authoritative `StateVersion`.
10. The lease owner resolves the user's exact required UserServices, reconciles
    remote Aevatar-managed keys, stores or updates Vault material when needed,
    and dispatches the typed actor command. Remotely observed obsolete keys are
    carried as typed cleanup intents in that command; they are not deleted
    before credential commit.
11. After remote validation, dispatch the narrow typed
    `ConfirmReadiness(RemoteValidated)` command for the complete validated
    descriptor and wait for an exactly matching committed descriptor. Rotation
    has already atomically committed the exact previous-key and previous-Vault
    cleanup plus every validated obsolete cleanup intent. Only after matching
    observation does Application retry those Actor-owned tracks and explicitly
    complete successful tracks by exact `(ApiKeyId, SecretRef)` identity.
    Same-key cleanup facts share exactly one NyxID track but preserve one Vault
    track per distinct locator; the exact previous Actor cleanup owns the
    NyxID track during rotation.
    Cleanup timeout or rejected completion remains best effort and cannot
    suppress the committed ready credential in Normal or Force mode. A Normal
    cleanup owner releases its mutation lease before dispatching
    `CurrentStateConfirmed`, making that committed event the event-driven
    handoff to a waiting Force caller.
12. Release the observation and any remaining mutation lease.
13. Pass the observed committed descriptor to the chrono transport and continue
    the original `codex_exec`.

There is no `Task.Delay` loop, query-time replay, read-model priming, or use of
an uncommitted method-local descriptor as execution authority.

One absolute outcome deadline is anchored immediately before each distributed
lease acquisition attempt. A Force reacquisition gets a fresh anchor, but a
slow lease response consumes its own primary budget rather than extending work
past the Garnet TTL. `MutationCompletionSeconds` bounds primary work. The fixed
lease additionally reserves ten seconds for compensation, ten seconds for
durable Actor recording, and ten seconds of lease-safety margin; configuration
therefore requires
`MutationLeaseSeconds >= MutationCompletionSeconds + 30`.

Pre-mutation work is bounded by the primary deadline and caller cancellation.
After the first irreversible external mutation, compensation and Actor cleanup
recording use their later absolute reserves; no phase receives a new
full-duration timeout. The explicit Provision, Rotate, and Revoke APIs use the
same pre-acquisition anchor and phase boundaries, including reconciliation and
compensation, while preserving their accepted-only response contract.

## Automatic Repair

The lifecycle automatically handles these states:

- **No credential:** create one exact two-service key, store it in Vault,
  commit it, observe it, and continue.
- **Legacy single-service key:** update the existing NyxID key to the exact
  sandbox-plus-LLM grant, verify the persisted policy, commit a policy
  reconciliation event, observe it, and continue.
- **Expired or revoked key:** create a fresh finite key, atomically commit it
  with cleanup intents for the observed obsolete artifacts, then retry only the
  Actor-owned tracks and continue.
- **Missing Vault secret:** when a current user bearer is available, rotate or
  replace the remote key, store the new one-time secret, atomically commit the
  replacement plus cleanup for the observed key/reference, and continue.
- **Same-locator reference drift:** if the same API key and deterministic Vault
  locator resolve to newer reference metadata than the committed descriptor,
  replace the credential and become ready in the same call. The stale
  descriptor is never certified, and the active key/reference is untouched
  before replacement commit.
- **Vault authority unavailable:** `Unauthorized`, `AuthenticationFailed`,
  `KeyringMismatch`, and `UnsupportedAlgorithm` are typed availability
  failures. They stop repair with `managed_credential_vault_unavailable` and
  never authorize revoking or replacing the recoverable NyxID key.
- **Ambiguous prior dispatch:** reconcile the exact remote key and deterministic
  Vault reference before issuing another key.
- **Manual reconciliation replacement:** manual provision/rotation never routes
  a remotely listed validation-failed or deterministic-Vault-missing key through
  issuance compensation. It carries that observed key as typed cleanup on the
  subsequent credential command, and a rejected command deletes nothing. An
  active reserved entry without a stable nonblank key ID fails closed before
  create, rotate, explicit revoke, pending-cleanup mutation, Vault mutation, or
  Actor dispatch. Every bearer-authorized pending-cleanup retry shares this
  read-only preflight, and every post-issuance or policy-repair relist repeats
  the same validation before Vault or Actor mutation. Only the exact stable
  nonblank ID returned by the local create or rotate may enter issuance
  compensation. Post-commit best-effort cleanup skips mutation and leaves the
  cleanup fact pending when the preflight fails, without suppressing the already
  committed ready credential.
- **Duplicate or orphaned Aevatar-managed keys:** keep an unambiguous committed
  valid key when possible; otherwise derive each orphan key's deterministic
  Vault reference, create one fresh credential, and atomically commit the new
  credential with cleanup intents for all observed obsolete keys. No observed
  orphan key or Vault locator is deleted before that commit. After the exact
  committed snapshot is observed, Application attempts the Actor-owned tracks.
  Cancellation between tracks leaves the already committed remainder pending.
  Each cleanup fact has an exact `SecretRef`; one fact per API key owns
  `NyxIdPending`, while every distinct locator may own `VaultPending`.
- **Cleanup conflicts:** repair selection never adopts a remote credential
  targeted by pending cleanup. Provision, rotation, policy reconciliation, and
  readiness confirmation reject an incoming/current descriptor whose API key
  or exact Vault locator is targeted by an active pending track.
- **Pending cleanup with a ready current credential:** retry cleanup
  best-effort, but do not block Codex execution solely because an obsolete key
  or Vault record is still pending deletion. The internal cleanup attempt
  reserves the final ten seconds of the shared outcome deadline for structural
  confirmation and committed handoff. Reaching that cleanup boundary leaves the
  cleanup pending and still emits `CurrentStateConfirmed`. The same
  best-effort rule applies after Force validation and after a committed
  replacement; caller cancellation at the cleanup boundary remains distinct
  from the internal cleanup timeout.
- **Manual compensation expiry:** if revoke or issuance compensation reaches
  its phase boundary, unknown and unattempted tracks are classified as pending
  and dispatched with the independent durable-recording reserve. Rejected or
  expired cleanup recording returns the stable persistence-pending failure
  instead of discarding the external outcome. Manual revoke also maps a thrown
  recording port, recording-token cancellation, or rejected admission to that
  same failure after destructive work.

NyxID mutations are re-read and validated before Actor dispatch. Policy
comparison is order-independent and requires exactly the two expected IDs.

## Concurrency

The existing cluster-shared Garnet mutation lease remains the sole external
mutation serializer for a NyxID owner.

Concurrent invocations behave as follows:

- one invocation acquires the mutation lease and performs the external work;
- other invocations subscribe to the same owner-scoped committed readiness
  stream;
- a sufficient committed snapshot completes a waiter without mutation;
- a Force waiter may use one committed structural confirmation to attempt the
  distributed lease exactly once, carry that triggering snapshot through
  acquisition, then perform one re-read before remote work;
- when that one re-read lags the triggering snapshot, the triggering committed
  snapshot remains the authoritative fallback; when the re-read is newer, the
  newer snapshot wins;
- if that one attempt remains busy, the waiter only awaits later
  `RemoteValidated` evidence and does not spin;
- one external mutation is performed per lease owner;
- every still-active invocation receives a committed descriptor with evidence
  sufficient for its requested mode and continues its own Codex execution.

No process-local owner-to-task dictionary is introduced. Projection Session
leases and the distributed mutation lease carry all cross-request coordination.

## Execution Retry

A structurally ready descriptor uses the normal fast path. If the transport
returns either:

- an exact managed proxy authorization denial; or
- an unavailable/revoked/missing Vault secret;

the Application coordinator may run one forced remote credential validation and
repair using the current caller bearer, then retry the chrono request once.

The retry is not used for timeout, capacity, malformed output, non-zero Codex
exit, cancellation, or arbitrary terminal failures. A second authorization
failure is returned directly. This prevents an infinite mutation or execution
loop.

## Failure Semantics

- `managed_target_disabled`: global kill switch is off.
- `managed_feature_not_enabled`: the user is outside the configured eligibility
  policy; no external mutation occurs.
- `managed_user_authorization_unavailable`: first provisioning or repair needs
  the current user's bearer, but none is available.
- `nyxid_identity_mismatch`: bearer ownership differs from the typed caller.
- `managed_user_services_unavailable`: one of the two required UserServices is
  missing, ambiguous, inactive, or unusable.
- `managed_credential_commit_timeout`: no ready committed snapshot was observed
  within the bounded mutation window.
- `managed_credential_vault_unavailable`: Vault authority or cryptographic
  availability prevents safe validation; no recoverable remote key is replaced.
- existing Vault, NyxID, proxy, timeout, capacity, malformed-output, and
  cancellation failures retain sanitized typed mappings.

Users are never told to call a provisioning endpoint or retry because the
normal lifecycle returned `provisioning`. Infrastructure failures may still
fail the execution, but credential setup is not exposed as a user workflow.

Caller cancellation stops the pending Codex execution. Once an irreversible
NyxID or Vault mutation has begun, the shared absolute outcome deadline still
drives the mutation to an Actor-recorded or compensating outcome. Caller
cancellation observed before irreversible work propagates without starting
cleanup or replacement.

## Manual Lifecycle API

The credential status, provision, rotate, and revoke endpoints remain available
for diagnostics, explicit credential rotation, emergency recovery, and
revocation. They are not prerequisites for tool use and are removed from the
normal rollout instructions.

The normal user-facing contract is only `codex_exec`.

## Tests

Tests must prove:

- `Allowlist` and `All` validation, including mutually exclusive allowlist
  semantics;
- an eligible new user's first call creates the exact dual-service key, stores
  its only persistent raw-key copy in Vault, observes committed state, and
  completes Codex in that same call;
- two users resolve different UserService IDs, API keys, Vault references, and
  actor identities;
- an already-ready credential performs no lifecycle mutation and does not
  require a caller bearer;
- concurrent first calls perform one external mutation and both execute;
- a legacy single-service key is updated and committed without user action;
- expired, missing-remote, missing-Vault, ambiguous-dispatch, duplicate-key,
  and orphan-key states converge to one ready credential;
- pending obsolete cleanup does not block a ready current credential;
- an exact authorization or Vault failure triggers at most one repair retry;
- ineligible users, missing first-use bearer, identity mismatch, and missing
  UserServices fail closed before unsafe mutation;
- committed readiness is delivered through the Projection Pipeline without
  polling, replay, or query-time projection priming;
- Application owns readiness and retry orchestration while Infrastructure owns
  only NyxID/Vault/chrono adapters;
- no raw agent key, interactive bearer, or delegation token appears in
  protobuf, Actor state, projection documents, logs, exceptions, API responses,
  workflow output, or serialized test snapshots.

Focused tests cover options, lifecycle, actor transitions, projection
observation, Application coordination, Infrastructure transport, Host
composition, endpoint behavior, and architecture boundaries. Changes to tests
must pass the repository stability and projection guards.

## Documentation And Rollout

The canonical managed Codex document and rollout runbook are updated to state:

- eligible users do not provision manually;
- the persistent key grants exactly the user's sandbox and LLM UserServices;
- `All` still depends on required UserService availability;
- the first interactive invocation supplies the bearer needed for transparent
  credential creation;
- later background invocations can use the committed Vault-backed credential;
- operations configure eligibility and platform services but never receive a
  user's raw key.

## Out Of Scope

- creating a missing NyxID UserService without a published NyxID contract;
- moving the persistent agent key into chrono-sandbox or the runner;
- replacing the existing gVisor runtime model;
- changing Codex workflow arguments;
- introducing a global shared agent key;
- broad public-rollout security changes tracked separately from this internal
  transparent-readiness migration.
