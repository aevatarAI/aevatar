---
title: "NyxID Workflow Admission Rollout"
status: active
owner: platform
---

# NyxID Workflow Admission Rollout

## Purpose and safety boundary

This runbook moves managed workflows from proofless-compatible `Shadow` mode to
proof-required `Enforce` without hot-rewriting definition or run actor state. It
uses the same v3-capable binary for rollout and rollback.

The three decisions are independent:

1. dynamic exposure uses `x-aevatar-tool` to decide which request-local LLM
   tools exist;
2. workflow admission resolves `user_service_id + operation_id` against the
   live exact contract and commits a call-site v3 proof;
3. runtime authorization permits managed raw `nyxid_proxy` only with that exact
   proof.

Do not infer one decision from another. Do not migrate in a query path, replay
events to fabricate proofs, edit actor state, or downgrade to a v2-only binary.
An accepted revision, schedule, or deployment receipt is not completion
evidence; read back the corresponding current-state projection.

## Configuration

The typed configuration key is:

```text
Aevatar:NyxId:ManagedWorkflowAdmissionMode = Shadow | Enforce
```

The Mainnet deployment environment-variable form is:

```text
AEVATAR_Aevatar__NyxId__ManagedWorkflowAdmissionMode=Shadow
```

`Shadow` remains the provider default for non-Mainnet hosts. The Mainnet image
loads `appsettings.Distributed.json`, which explicitly selects `Enforce`; a
deployment can use the environment key above for same-binary rollback. Both the
proxy tool and startup inventory guard consume the same configured
`NyxIdToolOptions` singleton. `Enforce` returns
`NYXID_OPERATION_ADMISSION_REQUIRED` before token resolution, exact-service reads,
file ingress, or proxy HTTP when a managed call lacks a valid proof. Ordinary
non-workflow human sessions keep their existing raw proxy behavior.

## Bounded telemetry

Use Meter `Aevatar.AI.ToolProviders.NyxId` and counter:

```text
aevatar.nyxid.proxy.admission.decisions
```

The complete tag allowlist and value domains are:

| Tag | Values |
| --- | --- |
| `aevatar.nyxid.admission.mode` | `shadow`, `enforce` |
| `aevatar.nyxid.admission.managed` | boolean |
| `aevatar.nyxid.admission.proof_present` | boolean |
| `aevatar.nyxid.admission.invocation_surface` | `human_session`, `workflow_tool_call`, `workflow_llm_tool_loop`, `unspecified` |
| `aevatar.nyxid.admission.risk` | `read_only`, `write`, `destructive`, `unspecified` |
| `aevatar.nyxid.admission.would_approve` | boolean |
| `aevatar.nyxid.admission.would_block` | boolean |

Do not add tokens, bodies, headers, paths, prompts, user/service IDs, actor IDs,
or any other high-cardinality value. The startup blocker is a separate bounded
diagnostic and is not a metric dimension.

## Phase 0: deploy Shadow

1. Deploy the candidate binary with mode explicitly set to `Shadow`.
2. Confirm Studio current-turn calls discover marked operations through the
   request-local `nyxid.connected_services` tool set. Raw `nyxid_proxy` must not
   be in the Studio role's model-visible `allowed_tools`.
3. Observe the decision counter for a complete release observation window.
4. Investigate every managed `would_block=true` sample by bounded aggregate
   dimensions. Do not add identity tags to locate an individual call; use the
   existing typed invocation receipt/audit under the approved access boundary.
5. Require new and rebound workflow definitions to use
   `external-capability-admission.v3`. Missing marker and operation-level
   `x-aevatar-tool: false` remain denied.

Do not proceed while proofless managed decisions are non-zero or while an
unreviewed marker/contract failure remains.

## Phase 1: inventory and rebind

### Startup inventory contract

Run the same candidate binary in `Enforce` as a non-serving preflight instance
against the production-equivalent read-model boundary. Startup paginates and
validates:

- every definition binding except an exact service definition whose actor-owned
  deployment current state is explicitly `Deactivated`;
- every run current state whose status is not `completed`, `failed`, or
  `stopped`.

The guard reparses each retained root and inline YAML through the canonical
workflow parser and compares those call sites with the persisted plan. An object
with no external call sites may omit the plan (or carry an empty plan). Every
object with an external call site must carry a complete, digest-valid
`external-capability-admission.v3` plan and valid typed execution policy. Missing
deployment evidence, `Active`, `Failed`, or unknown deployment state remains fail
closed. A service-invocation schedule pinned to a callable revision is therefore
covered by the active deployment's exact `PrimaryActorId`; a direct actor schedule
is covered by the ordinary definition binding.

On failure, startup throws the stable bounded form:

```text
CAPABILITY_ADMISSION_REBIND_REQUIRED: definitions=<count> active_runs=<count> definition_samples=[<up to 8 actor IDs>] active_run_samples=[<up to 8 actor IDs>]
```

`Shadow` performs no startup inventory scan. The guard never activates a
projection, primes a query, replays events, or mutates a document.

### Rebind a workflow service revision

For each serving v2 workflow revision:

1. Read the canonical service/revision and serving views. Preserve the exact
   service identity and workflow YAML; do not copy an old admission plan.
2. Create a new workflow revision through
   `POST /api/services/{serviceId}/revisions`. The existing endpoint performs
   live exact-contract admission. Use a new revision ID and the same reviewed
   YAML. Durable admission can contain only operations whose typed policy allows
   durable execution; write/destructive operations without a durable grant path
   must return the typed interactive-execution remediation.
3. Prepare and publish through the existing endpoints:

   ```text
   POST /api/services/{serviceId}/revisions/{revisionId}:prepare
   POST /api/services/{serviceId}/revisions/{revisionId}:publish
   ```

4. Activate the new revision with
   `POST /api/services/{serviceId}:activate`, then read the deployment view until
   the new deployment is `Active`.
5. Repoint the default or weighted serving set using the existing
   `:default-serving` or `:serving-targets` endpoint. Read back `/serving`; do not
   treat the accepted command as the cutover fact.
6. Repoint every affected schedule with a full reviewed
   `PUT /api/schedules/{scheduleId}` configuration whose
   `serviceInvocation.revisionId` is the v3 revision. Preserve its owner, auth,
   cron, timezone, payload, and enabled state from the authoritative desired
   configuration. Read the schedule current state before re-enabling or firing.
7. After all serving targets and schedules have moved, deactivate the old
   deployment through
   `POST /api/services/{serviceId}/deployments/{deploymentId}:deactivate` and
   read back `Deactivated`. That typed fact makes its historical definition
   binding ineligible for the startup gate.

The persisted-plan path intentionally returns
`CAPABILITY_ADMISSION_REBIND_REQUIRED` for v2. Resubmitting the same YAML without
the old plan is the online migration path; there is no separate migration
command. Non-service definitions must be rebound or retired through their owning
lifecycle before `Enforce`.

### Drain runs

Let existing v2 runs reach `completed`, `failed`, or `stopped`. Paused, waiting,
running, compensating, and unknown statuses are non-terminal and block this
release's `Enforce` startup. Do not hot-rewrite or replay them.

If a v2 run cannot drain, keep it on an old worker only behind a truly separate
deployment and read-model boundary. This release has no typed shared-inventory
isolation exemption: a legacy run visible to the Enforce host still blocks
startup. Do not call a shared-store worker pool “isolated.”

Repeat the non-serving `Enforce` preflight until it starts without the blocker.

## Phase 2: canary matrix

Keep production serving in `Shadow` while running the matrix on the candidate
binary and v3 revisions:

| Canary | Required result |
| --- | --- |
| Interactive read | Marked read-only operation dispatches with exact proof and no approval. |
| Interactive write | Existing approval middleware yields; zero proxy request occurs before the matching grant; one request occurs after resume. |
| Destructive denial | Denying/canceling approval produces no proxy request and no success claim. |
| Durable write/destructive | Definition admission rejects it with the interactive-execution remediation while no durable grant contract exists. |
| Scheduled read | A schedule pinned to the v3 revision fires, uses the proof-bound path, and reaches a projected terminal run. |
| Restart/resume | Restart while an approval is pending; only the matching committed continuation resumes, without duplicate dispatch. |
| Studio current turn | Marked per-operation tool remains available for each caller; unmarked operation and raw `nyxid_proxy` are absent. |
| Shadow rollback rehearsal | The same binary starts in `Shadow`; ordinary human raw proxy and v3 managed calls retain their expected behavior. |

Capture only run/revision/schedule identifiers in the controlled change record,
plus projected status/version and aggregate metric evidence. Do not capture
arguments, output bodies, credentials, or full proxy paths.

## Phase 3: enable Enforce

Proceed only when all of these are true:

- the observation window reports zero managed `would_block=true` decisions;
- the non-serving `Enforce` startup preflight passes;
- every callable definition is valid v3;
- every visible non-terminal run is valid v3;
- old v2 deployments are deactivated and schedules/serving sets point to v3;
- the complete canary matrix passes.

Change only the configuration value to `Enforce` and roll the same artifact.
Confirm readiness, then verify the decision counter reports `mode=enforce`. A
proofless managed canary must return `NYXID_OPERATION_ADMISSION_REQUIRED` with
zero downstream interaction.

## Rollback

Rollback is `Enforce -> Shadow` on the same v3-capable binary. Do not downgrade
to a binary that cannot deserialize or validate v3 state.

1. Set `AEVATAR_Aevatar__NyxId__ManagedWorkflowAdmissionMode=Shadow`.
2. Roll the same image/digest.
3. Confirm readiness and `mode=shadow` telemetry.
4. Keep v3 definitions, revisions, serving targets, and schedules in place. Do
   not restore v2 plans or reactivate v2 deployments.
5. Investigate the blocking call before attempting `Enforce` again.

Rollback restores legacy proofless behavior temporarily; it is not permission
to stop the v3 rebind/drain work.
