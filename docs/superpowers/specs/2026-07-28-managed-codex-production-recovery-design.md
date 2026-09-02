# Managed Codex Production Recovery Design

## Status

Approved on July 28, 2026.

This design restores reliable `codex_exec` execution across Aevatar, NyxID,
chrono-sandbox, and OpenSandbox without extending the current Workflow actor
turn with hidden credential repair.

## Production Evidence

Four production workflow runs exercised the managed target and failed through
three distinct boundaries:

- an Aevatar `NyxIdApiClient` inherited `HttpClient`'s 100-second timeout and
  aborted a long request before chrono-sandbox could return its terminal result;
- chrono-sandbox returned HTTP 502 after an execution deadline because the
  partial Codex JSON event stream was classified as malformed instead of timed
  out; and
- two otherwise stable runs spent approximately 240 seconds in managed
  credential readiness and ended with `managed_credential_commit_timeout`.

The credential status endpoint reported `status=active` and `state_version=7`,
but the execution path requires more than the enum value: exact owner identity,
a valid typed Vault reference, distinct sandbox and LLM UserService IDs, the
fixed chrono service slug, and a future expiry. The status contract therefore
did not prove that the descriptor was usable for execution.

The workflow timeout event did not interrupt the long readiness call because
the Workflow actor was synchronously awaiting the tool inside its current turn.
The timeout message could only be processed after that turn completed.

## Decision

Apply a staged recovery.

1. A normal `codex_exec` invocation consumes only an already committed,
   structurally execution-ready credential read model. It does not create,
   repair, rotate, or remotely validate a credential inside the Workflow actor
   turn.
2. The existing authenticated credential lifecycle endpoints remain the
   explicit provisioning, reconciliation, rotation, and revocation boundary.
3. The status endpoint reports credential lifecycle state separately from
   execution readiness and exposes a stable, non-secret reason when readiness
   is false.
4. Aevatar's per-call and HTTP-client deadlines cover chrono-sandbox's complete
   request lifecycle instead of only its inner Codex execution.
5. chrono-sandbox requires no additional business-code change for this
   recovery. The managed Codex branch containing timeout classification and
   confirmed cleanup must be merged and deployed.
6. Converting long Workflow tools to event-driven continuations is a separate
   architecture change. It is not mixed into this production recovery.

This intentionally replaces the previous transparent first-use repair decision.
The production evidence shows that a potentially 240-second mutation and
projection wait is not safe inside the current non-reentrant Workflow actor
turn. Explicit lifecycle operations make the mutation boundary honest until the
Workflow tool protocol becomes continuation-based.

## Alternatives

### Selected: explicit readiness before execution

Execution reads one committed current-state projection and fails quickly when
it is not usable. An authenticated caller repairs the credential through the
existing lifecycle API, waits for the accepted mutation to become visible in
the status read model, and then starts the workflow.

This is the smallest change that removes the 240-second hidden mutation from
the actor turn while preserving Actor authority, the Projection Pipeline, and
read/write separation.

### Rejected: only increase timeouts

Raising the 100-second transport ceiling would allow longer chrono requests but
would leave `active` status ambiguity and the hidden 240-second readiness wait.
The same failure would recur whenever a committed descriptor is incomplete or
its distributed mutation lease is contended.

### Deferred: continuation-based Workflow tool execution

The long-term model dispatches a tool operation, ends the current actor turn,
and resumes from typed completion or timeout events. It requires coordinated
changes to Workflow run state, tool receipts, recovery, timeout correlation, and
projection semantics. It is the correct architectural destination but too broad
for the current production recovery.

## Aevatar Architecture

The normal execution path becomes:

```text
Workflow codex_exec
  -> ManagedCodexExecutionCoordinator
       -> IManagedCodexCredentialQueryPort
       -> ManagedCodexCredentialReadiness assessment
       -> IManagedCodexChronoTransport
            -> ISecretVault
            -> NyxID proxy
            -> chrono-sandbox POST /codex/execute
```

The explicit mutation path remains:

```text
POST /api/managed-codex/credential or /rotate
  -> IManagedCodexCredentialLifecycle
       -> distributed mutation lease
       -> NyxID and Vault reconciliation
       -> typed credential Actor command
       -> committed state event
       -> Projection Pipeline
       -> current-state credential read model
```

No query performs mutation, projection priming, event replay, or synchronous
read-model refresh. No process-local owner-to-operation registry is introduced.

## Readiness Contract

One Application-owned readiness assessment is shared by the status endpoint and
the execution coordinator. This prevents the two surfaces from drifting again.
The assessment reads only the committed credential snapshot and the current
time. It never resolves a Vault secret or contacts NyxID.

The existing `status` field retains its single lifecycle meaning:

- `not_provisioned`;
- `active`;
- `expired`; or
- the lower-case committed credential status.

The response adds:

- `execution_ready`: boolean;
- `execution_readiness_reason`: one stable string; and
- the existing authoritative `state_version` and `cleanup_pending` values.

The readiness reason is one of:

| Reason | Meaning | Operator action |
| --- | --- | --- |
| `ready` | The committed descriptor satisfies every structural execution invariant. | Execute the canary. |
| `managed_target_disabled` | The global managed target is disabled. | Complete prerequisites, then enable it. |
| `managed_feature_not_enabled` | The authenticated user is outside the configured rollout policy. | Correct the internal allowlist or policy. |
| `managed_credential_not_provisioned` | No committed descriptor exists. | Call the existing credential POST endpoint. |
| `managed_credential_inactive` | The committed descriptor is not active. | Reconcile or rotate explicitly. |
| `managed_credential_expired` | Its committed expiry is not in the future. | Rotate explicitly. |
| `managed_credential_owner_invalid` | The descriptor owner does not equal the authenticated native NyxID owner. | Inspect Actor/projection integrity; do not guess an identity conversion. |
| `managed_credential_reference_invalid` | The typed Vault reference is incomplete or inconsistent with owner and expiry. | Reconcile through the authenticated lifecycle endpoint. |
| `managed_credential_service_binding_invalid` | API-key identity, exact sandbox/LLM UserService identities, or fixed service slug is invalid. | Reconcile through the authenticated lifecycle endpoint. |

The response never exposes the API key, raw secret, Vault locator, fingerprint,
or bearer token. Pending cleanup does not by itself make the current credential
unready; it remains a separately reported committed cleanup fact.

## Normal Execution Flow

1. Validate the managed target, empty Git workspace, timeout, and native NyxID
   caller identity.
2. Validate the kill switch and rollout eligibility.
3. Read the owner-scoped committed credential snapshot once.
4. Evaluate the shared readiness contract.
5. If readiness is false, return the stable reason immediately. Do not bind a
   readiness observation, acquire a mutation lease, call NyxID, mutate Vault,
   or dispatch an Actor command.
6. Resolve the ready descriptor's secret only inside the transport's bounded
   `ISecretVault.UseAsync` scope.
7. Send the fixed chrono request through the user's exact `chrono-sandbox`
   UserService.
8. Return chrono's terminal result or the existing sanitized transport failure.

An HTTP authorization or credential-unavailable response no longer triggers an
automatic remote-validation repair inside the same workflow turn. It returns a
stable failure and directs the operator or authenticated user to the explicit
credential lifecycle operation.

## Explicit Repair Flow

The existing `POST /api/managed-codex/credential` remains the idempotent
provision/reconciliation action. The existing `/rotate` action remains the
forced key-replacement action. No new `/repair` alias is added.

These write APIs may acquire the distributed mutation lease, validate the
current user bearer, call NyxID, mutate Vault, dispatch typed Actor commands,
and wait within their documented mutation budget. Lease contention remains an
immediate `managed_credential_mutation_in_progress` conflict for an explicit
caller; workflow execution does not silently wait for the lease TTL.

Because the write response is accepted-only, readiness is proved by a later
status read whose `state_version` comes from the authoritative credential Actor
and whose `execution_ready` is true. Operational polling, if used by a rollout
script, stays outside Workflow business execution and must be bounded.

## Timeout Budget Contract

The complete managed request includes sandbox creation/readiness, fixed
workspace setup, Codex execution, response forwarding, and confirmed cleanup.
The following production budget chain is required for the 180-second maximum:

| Boundary | Target | Purpose |
| --- | ---: | --- |
| chrono Codex execution | 180 s | Inner command deadline. |
| chrono lifecycle allowance | up to 270 s | Adds readiness, cleanup, fixed setup, and operational headroom. |
| Aevatar managed per-call deadline | 300 s | `timeout_seconds + 120 s` lifecycle allowance. |
| NyxID/ingress non-streaming proxy timeout | at least 315 s | Must not terminate before Aevatar's managed deadline. |
| Aevatar NyxID `HttpClient` ceiling | 330 s | Transport backstop above all per-call deadlines. |
| Workflow canary timeout | at least 360 s | Temporary outer budget until tools use continuations. |

`ManagedCodexOptions` names the 120-second allowance
`ExecutionLifecycleGraceSeconds`; it is not described as cleanup-only grace.
The chrono transport creates a linked deadline from the caller token and
`request.TimeoutSeconds + ExecutionLifecycleGraceSeconds`.

`NyxIdToolOptions.MaxRequestDurationSeconds` defaults to 330 and configures the
typed `NyxIdApiClient`. Manually constructed clients owned by the adapter use
the same ceiling. A caller-supplied `HttpClient` is not mutated.

The per-call token remains authoritative below the `HttpClient` ceiling. A
shorter caller cancellation still stops the request. An unanswered upstream is
mapped to the existing sanitized `managed_proxy_timeout` result.

## chrono-sandbox Deployment Contract

No new chrono-sandbox source change is required by this design. Deploy a build
that contains at least `feat/managed-codex-execution@1e8134d`, which:

- classifies an execd deadline as HTTP 504 `CODEX_EXECUTION_TIMEOUT` before
  parsing incomplete Codex JSONL;
- confirms sandbox deletion before returning the classified terminal result;
  and
- lets cleanup failure override a nominal execution result.

Production configuration must retain:

```text
MANAGED_CODEX_ENABLED=true
CODEX_TIMEOUT_MAX_SECS=180
CODEX_CLEANUP_TIMEOUT_SECS=30
SANDBOX_TIMEOUT_SECS=30
```

The immutable runner digest, fixed `chrono-llm-public` proxy URL, delegation
validation, concurrency, and resource profile remain rollout prerequisites.
Ingress and NyxID proxy timeouts are deployment configuration, not
chrono-sandbox business logic.

## Diagnostics and Failure Semantics

The Aevatar readiness failure log records only the stable reason, owner-scoped
Actor identity or correlation identifiers already allowed by policy, and the
authoritative state version. It never records bearer tokens, raw keys, Vault
locators, prompt text, or chrono raw output.

The important terminal distinctions are:

- a readiness reason means chrono-sandbox was not called;
- `managed_proxy_timeout` means Aevatar's complete per-call deadline elapsed;
- chrono HTTP 504 maps to the existing managed timeout failure and carries a
  sanitized diagnostic ID when supplied;
- `managed_proxy_unavailable` or target-unavailable failures identify an
  upstream/route problem rather than a credential mutation timeout; and
- `managed_credential_commit_timeout` may still occur on an explicit lifecycle
  write, but no longer consumes a normal Workflow execution turn.

## Testing

Automated verification must prove:

1. Status distinguishes `active` from `execution_ready` for every invalid
   descriptor category and exposes no secret material.
2. The status endpoint and execution coordinator use the same assessment and
   reason codes.
3. A non-ready normal execution performs one read-model query and performs no
   readiness observation, lease acquisition, NyxID/Vault mutation, Actor
   dispatch, or chrono request.
4. A ready execution uses the committed descriptor and reaches chrono once.
5. Transport authorization failure does not start automatic repair in the same
   workflow turn.
6. The managed transport waits through 299 seconds of its maximum lifecycle
   budget, stops after 300 seconds, and still honors earlier caller
   cancellation using `FakeTimeProvider` rather than wall-clock polling.
7. The typed NyxID client uses the 330-second ceiling, including configured
   overrides and manually owned construction.
8. Existing explicit lifecycle, projection, actor, and transport tests remain
   green.
9. `test_stability_guards.sh`, query/projection guards, architecture guards,
   documentation lint, focused builds, and focused tests pass.

chrono-sandbox verification runs `cargo fmt --check`, `cargo test`, and
`cargo clippy --all-targets --all-features` on the exact deployment commit. No
chrono file is changed solely to satisfy this Aevatar recovery.

## Rollout and Canary

1. Merge and deploy the chrono managed branch; confirm `/health`, OpenSandbox
   connectivity, timeout classification, and deletion confirmation.
2. Set the NyxID/ingress non-streaming proxy timeout to at least 315 seconds.
3. Deploy the Aevatar image with the readiness and budget changes while the
   managed rollout remains internal-only.
4. Call the authenticated credential POST endpoint for the canary user. Use
   `/rotate` only when reconciliation cannot preserve a valid key.
5. Read status until a newer authoritative `state_version` reports
   `execution_ready=true`; stop at a bounded operational deadline.
6. Run three distinct workflow canaries with a Workflow timeout of at least
   360 seconds: a trivial exact-output task, an approximately 80-second task,
   and a complex task allowed to use the full 180-second execution budget.
7. Verify the Workflow terminal result, Aevatar sanitized failure/result, chrono
   diagnostic ID, and confirmed sandbox deletion for every run.
8. Verify there is no 100-second local timeout, no 240-second hidden credential
   wait in normal execution, no misleading chrono 502 for a true deadline, and
   no secret in logs or read models.

Rollback disables Aevatar managed execution first. Status and revocation remain
available. chrono-sandbox can then be rolled back independently after all
one-shot sandboxes are accounted for.

## Deferred Work

A separate architecture design will convert long-running Workflow tools to the
required Actor continuation model:

```text
dispatch tool command -> end turn -> completion/timeout event -> reconcile keys
-> resume run -> commit terminal workflow event
```

That work must define typed operation identity, durable pending state, timeout
and late-result reconciliation, replay behavior, tool receipt projection, and
crash recovery. It must not reintroduce stream request/reply, query-time
priming, process-local registries, or callback-thread state mutation.
