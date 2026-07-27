# Owner-Aware Agent Key Cleanup Design

## Goal

Make the canonical owner-aware schedule API able to delete a Studio Team
member workflow schedule that owns a dedicated scheduled-invocation Agent Key,
complete NyxID and Vault revocation, and safely replay the same delete while
revocation is pending.

This repair unblocks the public
`aevatar-scheduled-agent-key-canary` skill without restoring the retired
nested Team automation CRUD routes.

## Problem

PR #2960 made `/api/schedules` the single schedule lifecycle surface and
reduced:

```text
/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
```

to preflight only. Production already enforces the new owner query:

```text
ownerKind=studio_member_automation
ownerScopeId={scopeId}
ownerTeamId={teamId}
ownerMemberId={memberId}
```

The mapped canonical delete currently accepts only `owner` and `reason`, then
calls the simple owner-scoped delete overload. That overload deliberately
rejects schedules with a credential lifecycle:

```text
team_automation_delete_requires_revocation_context
```

The rich Actor-owned delete and revocation path already exists, but the
canonical HTTP surface cannot supply its stable operation identity,
authenticated owner context, or fresh bearer-backed effect authority.

Therefore a schedule created specifically to use
`scheduled_invocation_agent_key` can fire, but an ordinary authenticated
caller cannot guarantee key revocation and terminal cleanup through the
canonical API.

## Semantic Decision

`/api/schedules` remains the only schedule CRUD and action API.

For a typed Studio member automation owner, `DELETE
/api/schedules/{scheduleId}` has two honest modes:

- An owner-only schedule without a credential lifecycle may continue using
  the simple owner-scoped delete behavior.
- A credential-lifecycle delete supplies both `operationId` and
  `idempotencyKey` and uses the existing Studio application orchestration
  that commits Actor deletion intent, executes NyxID/Vault effects with the
  current authenticated bearer, and reports the outcome back to the schedule
  Actor.

Repeating the exact same canonical DELETE body is the retry contract. It
reuses the unchanged owner tuple, `operationId`, and `idempotencyKey`. It does
not create a separate public retry route and does not mint replacement
operation identities.

## Architecture

### Host boundary

`ScheduledDispatchEndpoints.Delete` continues to own HTTP adaptation only.
It:

1. Parses the typed owner and rejects a scope mismatch before Application
   dispatch.
2. Rejects a partial lifecycle identity when only one of `operationId` or
   `idempotencyKey` is present.
3. Uses the existing simple delete path when both lifecycle identity fields
   are absent.
4. Uses the credential-aware Studio application port when both lifecycle
   identity fields are present.
5. Derives the authenticated NyxID owner and current bearer from trusted HTTP
   context. The request body cannot provide owner authority, binding identity,
   bearer, raw key, Vault reference, or other credential material.
6. Returns `202 Accepted` as admission only.

The security-sensitive owner/bearer resolution currently duplicated in the
retired nested handlers will be extracted into one Host-layer helper shared by
the canonical endpoint and the remaining nested preflight endpoint.

### Application orchestration

The canonical endpoint calls `IStudioMemberWorkflowSchedulePort.DeleteAsync`
with:

```text
scopeId
teamId
memberId
scheduleId
operationId
idempotencyKey
reason
AuthenticatedAuthorizationOwnerContext
ProvisioningBearerToken
```

The port remains responsible for:

- resolving the exact Team member through Studio read models;
- validating the complete owner tuple;
- invoking the rich `DeleteTeamAutomationAsync` Actor command;
- executing only the Actor-fenced pending NyxID/Vault revocation effects;
- recording completion or stable failure back to the authoritative schedule
  Actor.

No Host code writes schedule state, credential state, or read models.

### Actor and read-model semantics

The schedule Actor remains the sole authority.

On the first credential-aware delete it commits the deletion operation and
pending revocation before external effects. While either revocation track is
pending, the owner-aware read remains visible with the authoritative
`stateVersion`.

On an exact replay, the Actor correlates the same delete operation and may
grant a new fenced effect attempt. Payload drift or a different operation
identity conflicts instead of starting a second deletion.

The schedule becomes not found only after both required revocation tracks
reach terminal completion and the committed deletion is projected.

## HTTP Contract

Canonical lifecycle delete:

```http
DELETE /api/schedules/{scheduleId}
Content-Type: application/json

{
  "reason": "scheduled_agent_key_canary_cleanup",
  "operationId": "delete-operation-...",
  "idempotencyKey": "delete-idempotency-...",
  "owner": {
    "kind": "studio_member_automation",
    "scopeId": "scope-...",
    "teamId": "team-...",
    "memberId": "m-..."
  }
}
```

`operationId` and `idempotencyKey` have one meaning each and are required
together for the credential-aware branch. Unknown fields remain rejected.

The response exposes only non-secret admission fields:

```json
{
  "accepted": true,
  "status": "pending",
  "scheduleId": "sch-...",
  "operationId": "delete-operation-...",
  "commandId": "cmd-..."
}
```

The response never claims revocation completion. Callers reread the exact
canonical owner tuple.

## Replay and Recovery

The first delete and every retry use the byte-equivalent semantic body:

```text
scheduleId
owner.kind
owner.scopeId
owner.teamId
owner.memberId
operationId
idempotencyKey
reason
```

Only the transient authenticated bearer may change between attempts.

Recovery rules:

- `revocationPending=true`: repeat the same DELETE with a fresh authenticated
  request and unchanged stable identities.
- accepted receipt with stale read model: observe; do not invent another
  operation.
- owner mismatch: return not found without leaking the schedule.
- operation/idempotency mismatch after deletion starts: return conflict.
- missing owner binding or bearer: fail before Actor mutation.
- one completed track and one failed/pending track: preserve the row and retry
  only the remaining Actor-owned effect.

## Error Contract

Use stable, non-sensitive errors:

| Condition | HTTP | Code |
| --- | ---: | --- |
| Only one lifecycle identity field supplied | 400 | `INVALID_TEAM_AUTOMATION_REQUEST` |
| Missing authenticated NyxID subject or binding | 401 | `TEAM_AUTOMATION_UNAUTHORIZED` |
| Missing or malformed bearer | 401 | `TEAM_AUTOMATION_UNAUTHORIZED` |
| Exact owner or schedule not found | 404 | `TEAM_AUTOMATION_NOT_FOUND` |
| Different operation attempts to replace active deletion | 409 | `TEAM_AUTOMATION_CONFLICT` |
| Studio lifecycle capability not composed in the Host | 503 | `TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE` |

Error bodies must not expose bearer values, credential IDs, permission
digests, Vault references, or hidden owner identities.

## Ornn Canary Contract After Deployment

After the repair is deployed, update the skill to:

- treat `/api/schedules` as canonical and nested Team automation as
  preflight-only;
- use canonical owner-aware list/detail query parameters;
- use one canonical detail response for owner LLM, revocation-track, schedule,
  and `recentFires[].manual` evidence;
- call the canonical credential-aware DELETE and replay the same body while
  revocation is pending;
- keep `memberId`, `draftWorkflowId`, `publishedServiceId`, and `scheduleId`
  distinct;
- use `aevatar_get_schedule` only for its flattened lifecycle fields and never
  mix those names with raw canonical response fields;
- require recurring readiness from `scheduleMode`, `oneShotFireAt`, and
  `completed`;
- remove unsupported guarantees that the schedule-create tool exposes its
  internal idempotency key;
- continue requiring the same Agent Key's `last_used_at` transition,
  `manual=false`, workflow marker, and complete cleanup.

The previously planned fire-diagnostic follow-up is no longer needed because
the canonical owner detail already returns `recentFires`.

## Testing

Use TDD with distinct identities such as:

```text
memberId=m-alpha
workflowId=wf-alpha
publishedServiceId=svc-alpha
scheduleId=sch-alpha
```

Required tests:

1. Canonical DELETE with exact owner and stable lifecycle identities enters
   the rich credential-aware Application path.
2. A dedicated-key schedule no longer returns
   `team_automation_delete_requires_revocation_context`.
3. Exact replay uses the same operation and can continue pending revocation.
4. A changed operation or idempotency key conflicts.
5. A partial lifecycle identity is rejected before dispatch.
6. Authenticated owner and fresh bearer are derived from HTTP context, not
   request fields.
7. Owner mismatch remains not found.
8. The row remains visible until both NyxID and Vault tracks complete.
9. Nested Team automation CRUD/action routes remain absent.
10. Generic and owner-only schedule deletion behavior remains covered.
11. Response and error JSON contain no credential material.
12. Documentation and Ornn skill routes match the deployed contract.

Run at minimum:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter ScheduledDispatchEndpointsTests
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "StudioMemberWorkflowSchedulePortTests|StudioMemberAutomationEndpointsTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

## Non-Goals

- Do not restore nested Team automation list/detail/delete/retry routes.
- Do not add a second schedule or credential lifecycle.
- Do not directly create, rotate, or delete NyxID Agent Keys from the skill.
- Do not infer ownership from `serviceId`, display names, headers, or route
  position.
- Do not make an accepted receipt imply revocation or read-model completion.
- Do not migrate historical standalone schedules.
- Do not add operator-only repair behavior to the ordinary user path.

## Deployment and Acceptance

Push the runtime repair to `origin/feature/integrate` only after focused tests,
architecture guards, stability guards, and documentation lint pass.

After automatic deployment:

1. Probe the live canonical owner query and lifecycle DELETE contract.
2. Update and republish the Ornn candidate privately.
3. Require live Ornn validation and a completed green audit.
4. Execute one real annual cron occurrence through `/v1/responses`.
5. Require canonical authorization, `manual=false`, workflow marker, the same
   key's `last_used_at: null -> timestamp`, and terminal cleanup.
6. Make the exact Ornn GUID public only after every gate succeeds.
