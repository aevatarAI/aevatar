# Public Scheduled Agent Key Canary Skill Design

## Product Decision

Publish one public Ornn skill named
`aevatar-scheduled-agent-key-canary`. Its purpose is to let the current
authenticated Aevatar user prove that a canonical Studio Team member
automation can:

1. provision a dedicated, constrained NyxID Agent Key;
2. fire from a real wall-clock cron without `run-now`;
3. execute one harmless `llm_call` through that key; and
4. revoke the key and clean up every temporary resource created by the
   canary.

The skill is a diagnostic client of existing Aevatar and NyxID contracts. It
does not add another scheduling runtime, issue keys directly, accept raw
credentials, repair projections, or weaken authorization checks.

The result is account-scoped. A pass means the current user can use this
feature with the current owner LLM selection and current production
configuration. It is not a platform-wide health claim.

## Approaches Considered

### Tool-based, self-cleaning Ornn canary

Use the existing Aevatar Studio provisioning/query tools, NyxID API-key
inventory tool, and an exact owner-scoped Aevatar proxy read/delete path.

This is selected because it is usable from an authenticated agent session,
exercises the canonical Team member automation path, produces direct
credential-use evidence, and does not require users to prepare a local shell
script.

### CLI-only deterministic canary

Bundle a script that drives the same APIs through the local `nyxid` CLI.

This would simplify timestamp calculation and local evidence persistence, but
it would exclude users who can use Aevatar through an agent session without a
local CLI environment. It may be added later as a separate operator skill; it
is not the default public check.

### Documentation-only checklist

Explain how users can create a schedule manually in Studio and inspect it.

This is rejected because an agent can easily mistake `202 Accepted`, a
successful workflow result, or an existing key for proof that the scheduled
Agent Key path worked. The public canary needs an executable evidence
contract.

## Ornn Package Contract

The package is self-contained:

```text
aevatar-scheduled-agent-key-canary/
  SKILL.md
```

Its Ornn metadata declares:

- name: `aevatar-scheduled-agent-key-canary`;
- version: `1.0`;
- category: `tool-based`;
- tags covering Aevatar, NyxID, Agent Key, cron, schedule, canary, and
  diagnostics;
- a tool list containing only the Aevatar Studio/query and NyxID surfaces
  needed by the canary.

The package omits `metadata.output-type`; it is unnecessary for a tool-based
skill, and Aevatar's typed Ornn publisher treats it as a runtime-only field.
This skill returns a text verdict through normal agent output.

The expected tool set is:

- `aevatar_create_team`;
- `aevatar_create_member`;
- `aevatar_bind_member_workflow`;
- `aevatar_schedule_member_workflow`;
- `aevatar_get_member`;
- `aevatar_get_schedule`;
- `aevatar_list_schedules`;
- `nyxid_services`;
- `nyxid_api_keys`;
- `nyxid_proxy`;
- `code_execute`.

The skill contains no executable scripts, embedded credentials, service IDs,
environment variables, private production URLs, or user-specific fixtures.

`nyxid_proxy` and `code_execute` are broad platform tools. The route and code
allowlists below constrain agent behavior but are not a hard capability
sandbox. Publication therefore also requires the existing sandbox isolation,
the exact connected-service selection, the Ornn format validator, and a green
Ornn security audit. If a narrower typed tool becomes available, it should
replace the corresponding broad call in a later version.

## Exact Proxy Route Contract

Resolve one active connected Aevatar `UserService.id` with
`nyxid_services`; every `nyxid_proxy` call supplies that exact `service_id`,
`slug: "aevatar"`, and one of these routes only:

| Purpose | Method and path |
| --- | --- |
| Owner LLM selection | `GET /api/user-config/llm` |
| Team observation | `GET /api/scopes/{scopeId}/teams/{teamId}` |
| Draft observation | `GET /api/workspace/workflow-drafts/{draftWorkflowId}?scopeId={scopeId}` |
| Canonical automation detail | `GET /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}` |
| Canonical automation list | `GET /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations` |
| Scheduled member runs | `GET /api/scopes/{scopeId}/members/{memberId}/runs?take=10&scheduleId={scheduleId}&updatedFrom={utc}` |
| Fire-origin diagnostic | `GET /api/schedules/{scheduleId}?scopeId={scopeId}&teamId={teamId}&memberId={memberId}` |
| Delete automation | `DELETE /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}` |
| Retry revocation | `POST /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/retry-revocation` |
| Retire revision | `POST /api/scopes/{scopeId}/members/{memberId}/binding/revisions/{revisionId}:retire` |
| Delete member | `DELETE /api/scopes/{scopeId}/members/{memberId}` |
| Delete draft | `DELETE /api/workspace/workflow-drafts/{draftWorkflowId}?scopeId={scopeId}` |
| Archive Team | `POST /api/scopes/{scopeId}/teams/{teamId}/archive` |

Mutation bodies contain only the fields required by the live contract.
Automation delete and `retry-revocation` use the same
`{operationId,idempotencyKey}` body. The skill must not call generic
`/api/schedules` mutation routes, `run-now`, unrelated Aevatar APIs, or a
route missing the full owner tuple.

Before production mutation, validation must confirm these paths and response
fields against the connected Aevatar service. A missing route, changed field,
or changed status code is a fail-closed prerequisite error, not permission to
guess a replacement.

The fire-origin route is a current production compatibility read. It is used
only with the complete Team owner tuple and never for mutation. A follow-up
Aevatar issue should replace this dependency with a canonical Team-owned
diagnostic read or typed tool before the generic schedule compatibility branch
is removed.

## Preconditions And Confirmation

Before mutating anything, the skill must establish:

- the user is authenticated to Aevatar and NyxID;
- the current Aevatar scope is available from the trusted tool context;
- `code_execute` can run a fixed, non-secret Python clock probe before any
  production mutation;
- `GET /api/user-config/llm`, called through the exact connected Aevatar
  UserService, reports an explicit owner LLM selection backed by an active
  exact NyxID UserService;
- the `aevatar` NyxID service is active and selectable by exact
  `UserService.id` for the bounded HTTP reads and cleanup calls;
- no earlier resource with the new canary suffix exists.

The skill then presents one concise confirmation covering all intended
effects: create a temporary Team/member/workflow/schedule, allow one LLM call,
wait for the real cron, revoke the dedicated Agent Key, delete the temporary
member/draft, and archive the temporary Team.

If the user declines, the skill stops without mutation.

## Execution Phases And Continuation

The canary is intentionally multi-turn. Aevatar `/v1/responses` currently
allows at most eight local tool rounds per response and observes one response
for at most five minutes by default. A real cron target plus ordered cleanup
cannot honestly fit inside one such response.

The skill therefore uses these completed-response checkpoints:

1. prerequisite inspection and one mutation confirmation;
2. Team/member creation and workflow binding;
3. schedule arming and pre-fire Agent Key evidence;
4. post-fire evidence collection and automation deletion;
5. terminal revocation observation and scaffold cleanup.

Each checkpoint must complete before the eighth tool round and instruct the
caller to continue with the returned `previous_response_id` plus a
line-leading `::aevatar-scheduled-agent-key-canary <phase>` command. Repeating
the trigger makes the skill load again even when a client reconstructs only
bounded prior context. A checkpoint may include the non-secret resource ledger
needed for continuation, with each identity labelled by its exact semantic
type. It must not include credentials, permission digests, complete
inventories, raw tool responses, or provider payloads.

The schedule-arming phase computes its target only after the member binding is
read-model visible. The target must be at least eight full minutes after that
observation. The arming response returns the exact UTC continuation time and
asks the caller to continue no earlier than 15 seconds after the target minute
begins. If evidence or revocation is not yet visible, the skill returns another
completed checkpoint rather than exhausting the tool-round limit, duplicating
a mutation, or falling back to `run-now`.

Continuation is valid only through the same authenticated caller and response
session. If the continuation context is missing, the skill may resume only
from the labelled non-secret checkpoint ledger and owner-correct reads. It must
never reconstruct one identity from another or rediscover resources by display
name alone.

## Identity And Resource Ledger

Generate one random, non-secret canary suffix and derive distinct names for:

- Team display name;
- member display name;
- workflow draft name;
- schedule display name;
- workflow output marker.

Also derive distinct caller-supplied URL-safe IDs for the Team, member, and
draft workflow. Supplying those exact IDs allows owner-correct recovery if a
create response is lost; the IDs must have visibly different prefixes such as
`team-canary-`, `m-canary-`, and `wf-canary-`.

Use `code_execute` only for a fixed Python clock/random calculation with no
user-supplied code or environment access. The result supplies the UTC target,
suffix, and marker seed.

The skill keeps a request-local ledger containing only identities returned by
successful operations and emits the same allowlisted fields in continuation
checkpoints:

- `scopeId`;
- `teamId`;
- `memberId`;
- `draftWorkflowId`;
- `publishedServiceId`;
- `revisionId`;
- `bindingRunId`;
- `scheduleId`;
- create and delete operation/idempotency identities;
- the newly observed Agent Key ID and name;
- target fire time and redacted evidence timestamps.

These identities remain semantically distinct. The skill must never infer one
from another, assume equality, or send a draft workflow ID to a member API.
Checkpoint fields use the explicit names above; a generic `resourceId` or
ambiguous `workflowId` field is forbidden.

The ledger must not contain a raw Agent Key, bearer token, Vault reference,
credential ciphertext, refresh token, permission digest in final output, or a
complete NyxID inventory response.

## Temporary Workflow

Create a new Team and a workflow member owned by that Team. Bind a minimal
workflow with one `llm_call`:

```yaml
name: scheduled_agent_key_canary
description: Harmless one-call scheduled Agent Key canary.
roles:
  - id: canary
    name: Canary
    system_prompt: |
      Return the exact marker supplied in the user prompt and nothing else.
steps:
  - id: prove_agent_key
    type: llm_call
    target_role: canary
    allowed_tools: []
```

The scheduled prompt contains the unique marker. `allowed_tools: []` prevents
the temporary workflow from calling any tool or external connector. The only
external effect inside the run is the owner LLM request required to prove
Agent Key use.

Wait until the member read model reports the expected, distinct
`draftWorkflowId`, `publishedServiceId`, and active revision/binding facts
before scheduling. An accepted bind receipt alone is not sufficient.

## Schedule Creation

After binding readiness is visible, compute a UTC target at least eight full
minutes in the future. Use a five-field annual cron:

```text
<minute> <hour> <day-of-month> <month> *
```

The next occurrence must equal the chosen target minute and the following
occurrence must be at least 300 days later. This prevents a second fire during
the observation and cleanup window.

Create the automation only through `aevatar_schedule_member_workflow`. That
tool owns the canonical
`dedicated_scheduled_invocation_agent_key` provisioning path. Do not use:

- `aevatar_provision_workflow_schedule`;
- the legacy `scheduled_agent_creator`;
- generic schedule creation;
- `run-now`;
- direct NyxID API-key creation.

Create-time confirmation and idempotency remain owned by the typed Aevatar
tool. If the result is ambiguous, reread the exact member's schedules and
recover by the recorded operation identity. Never create a second schedule
with new identities to hide an uncertain first attempt.

## Pre-Fire Evidence

A pass candidate must reach all of these states before the target minute:

- the canonical member automation exists under the exact
  `scopeId/teamId/memberId`;
- `authorizationStatus == "active"`;
- `credentialSourceKind == "scheduled_invocation_agent_key"`;
- `enabled == true`;
- `nextFireAt` equals the selected target minute;
- `lastFireAt == null`;
- `revocationPending == false`;
- the owner LLM route/model/UserService fields match the current typed owner
  selection;
- one newly created NyxID Agent Key is active;
- both wildcard flags are false;
- its allowed service IDs contain exactly the selected owner LLM
  `UserService.id`;
- its `last_used_at` is null.

The skill compares NyxID inventory before and after schedule creation and
selects a candidate only when its ID was absent from the baseline, its
creation time falls between 30 seconds before the canary create attempt and
30 seconds after the post-create observation, its name starts with the
reserved `studio-schedule-` prefix, both wildcard flags are false, and its
allowed service IDs equal the selected owner LLM UserService singleton. There
must be exactly one candidate. Zero or multiple candidates fail closed. This
is an explicitly labelled unique candidate correlation, not a direct
schedule-to-key reference. The skill must not select a key merely because its
name has a familiar prefix.

## Real Cron Evidence

The skill must not call any `run-now` endpoint or tool. It waits through the
target minute, then uses bounded reads to establish:

- the canonical automation `lastFireAt` advanced from null;
- its authoritative `stateVersion` advanced;
- the exact member run list contains exactly one run for the canary
  `scheduleId`;
- the run has the live wire value `completionStatus == 1` (`Completed`),
  `lastSuccess == true`, successful status fields, and an empty `lastError`;
- the run `lastOutput` contains the unique marker;
- the same exact Agent Key remains constrained and its `last_used_at`
  changed from null to a timestamp after the target fire time.

For supplemental fire-origin evidence, call the existing owner-scoped
scheduled-dispatch diagnostic read with the complete owner tuple:

```text
GET /api/schedules/{scheduleId}?scopeId={scopeId}&teamId={teamId}&memberId={memberId}
```

This read must return one recent fire whose scheduled time equals the target,
whose error is empty, and whose `manual` value is false. The skill never uses
the generic schedule mutation surface, and it never performs this diagnostic
read without the full owner tuple.

No single observation is sufficient:

- `202 Accepted` proves admission only;
- the workflow marker proves execution only;
- `manual=false` proves cron origin only;
- the exact key's `last_used_at` transition proves Agent Key use.

The canary passes only when all four evidence classes agree.

All observations are bounded across completed-response checkpoints. Binding
and credential activation each receive at most two minutes, the cron
observation ends two minutes after the target minute, and cleanup receives at
most three minutes before returning `CLEANUP_INCOMPLETE`. A single response
must stop early enough to complete before its tool-round or wall-clock budget.
A timeout never causes a second create or a `run-now` fallback.

Between reads, `code_execute` may run only the fixed
`time.sleep(30); print("continue")` Python snippet. It must not receive
resource IDs, tool responses, credentials, user text, or dynamically
generated code. Failure of this pacing probe before mutation is a harness
prerequisite failure; it is not evidence that Agent Key scheduling is
unavailable.

## Cleanup And Compensation

Cleanup begins after evidence collection whether the canary passed or failed.
It uses only identities in the canary ledger and proceeds in this order:

1. Delete the canonical member automation with a fresh delete operation ID
   and idempotency key.
2. While the row remains visible, require exact NyxID/Vault revocation status
   values and retry only through `retry-revocation` with the original delete
   identity when the row is terminally retryable.
3. Accept automation disappearance only after the owner-correct detail is
   `404`, the owner list no longer contains the schedule, and the exact Agent
   Key is inactive or absent.
4. Retire the exact bound revision.
5. Delete the exact member and observe member `404`.
6. Delete the exact draft workflow and observe draft `404`.
7. Archive the exact Team and observe `lifecycleStage == "archived"`.

Use the canonical Team automation detail for the exact owner LLM selection
and the NyxID/Vault revocation-track values. The narrower
`aevatar_get_schedule` result is useful for ordinary schedule fields but is
not evidence for fields it does not expose. A successful draft delete may
produce an empty proxy result for HTTP `204`; the subsequent owner-correct
draft `404` is the completion evidence.

The Team remains as an archived lifecycle record because that is the
canonical Team cleanup contract. An observed
`lifecycleStage == "archived"` is terminal cleanup, not a residual resource
that changes the verdict to `CLEANUP_INCOMPLETE`.

The public canary does not require the operator-only
`6202/StudioMemberAutomationRevocationCompleted` log. Its cleanup verdict is
based on canonical deletion visibility plus exact NyxID key disappearance.
It must not claim a complete operational-audit trail.

The skill must never delete a resource discovered only by display-name
similarity. If the ledger lacks an identity, that cleanup step is skipped and
reported. If revocation is still pending, member/draft/Team cleanup stops so
the authority needed for retry remains reachable.

## Result Semantics

The final response starts with exactly one verdict:

- `PASS`: all pre-fire, cron, run, exact-key-use, and cleanup checks passed;
- `FAIL`: the feature check failed, but cleanup completed;
- `CLEANUP_INCOMPLETE`: one or more created resources remain or their terminal
  state cannot be established.

For a pre-mutation prerequisite or harness failure, use `FAIL` with
`featureConclusion=not_evaluated` and a stable prerequisite error code. This
does not claim that scheduled Agent Key execution is unavailable. For an
executed canary failure, use `featureConclusion=failed`. A successful canary
uses `featureConclusion=passed`.

The report includes only:

- target and observed UTC timestamps;
- booleans for canonical authorization, real cron, workflow marker, exact key
  use, and cleanup;
- schedule and run status counts;
- before/after `last_used_at` null/timestamp shape without exposing key
  material;
- stable error codes and the exact cleanup stage when applicable.

The report must not print raw tool responses or complete inventories. Final
resource IDs may be shown only when cleanup is incomplete and the user needs
the exact identity to recover. Intermediate continuation checkpoints may carry
the allowlisted labelled ledger defined above.

## Failure Handling

- Missing owner LLM selection or connected Aevatar service: stop before
  mutation and report `FAIL`, `featureConclusion=not_evaluated`, and the
  prerequisite.
- Bind or publication not visible: clean only the scaffold that was actually
  created.
- Schedule create response lost: recover through the original operation and
  exact member schedule list.
- Team, member, or bind response lost: query only the exact caller-supplied
  Team/member/draft identity and continue only when one owner-correct resource
  has the expected typed facts. Never create a replacement with a new ID.
- Target minute missed before the schedule becomes active: delete the
  automation; do not call `run-now` and do not reinterpret a later annual fire
  as this canary.
- Run failure or marker mismatch: collect the exact key state, then clean up.
- Key `last_used_at` unchanged: fail even when the workflow claims success.
- Revocation pending: preserve owner resources and return
  `CLEANUP_INCOMPLETE` with the retryable stage.
- Tool unavailable or contract field missing: fail closed rather than
  substituting an older scheduler/key model.

## Skill Validation Strategy

Skill authoring follows a RED-GREEN-REFACTOR validation loop:

1. Run baseline scenarios without the skill. At least one baseline must show
   the common false-positive behavior: using `run-now`, accepting a `202`,
   accepting workflow prose without checking the exact key, or omitting
   cleanup.
2. Load the candidate skill and rerun the same scenarios. The agent must use
   the canonical member automation tool, avoid `run-now`, require the exact
   key transition, and execute cleanup.
3. Run negative scenarios for missing owner LLM selection, ambiguous create,
   missed target minute, unchanged key usage, and revocation pending.
4. Validate the package with both the local skill validator and Ornn's live
   `/api/v1/skill-format/validate` endpoint.
5. Perform one authenticated production invocation with the current user's
   account and retain only an allowlisted, redacted result.

The forward tests may create production canary resources only after the same
explicit confirmation required by the published skill.

The validation invocation must exercise the completed-response continuation
contract rather than assuming one `/v1/responses` request can wait through the
cron and cleanup. Every intermediate response must remain resumable and must
stop before the eight-tool-round limit.

## Publication Flow

The source package lives at:

```text
~/Code/Ornn/skills/aevatar-scheduled-agent-key-canary/
```

Ornn creates new skills as private. Publication therefore uses this ordered
flow:

1. validate the ZIP package;
2. upload it as a new private skill;
3. read it back by exact GUID/version and verify its files, tool list, and
   hash;
4. trigger the Ornn security audit while the skill is private and require
   `status=completed` with `verdict=green`; a yellow verdict requires fixing or
   otherwise resolving the findings in a new skill version and re-running the
   audit until it is green; yellow and red both block publication;
5. run the authenticated canary once and confirm cleanup;
6. replace permissions with `isPrivate=false` and empty user/org share lists;
7. verify public keyword/semantic search;
8. verify the exact version can be read through Ornn's authenticated public
   catalog boundary and is not relying on an owner-only search scope;
9. reread and report the audit verdict attached to the public version.

If any post-ACL public read/search/audit gate fails, immediately restore the
exact GUID to private with empty share lists and report the rollback. If any
earlier gate fails, leave it private. In either case, do not describe the
skill as public or ready.

Production currently places all Ornn API access behind the authenticated
NyxID proxy. Public therefore means visible to every authenticated Ornn user,
not anonymous internet access. If a second non-owner profile is available,
use it for the final read; otherwise require the exact ACL response,
`scope=public` search result, and exact-version public-catalog read while
recording that cross-account verification was unavailable.

## Aevatar And NyxID Boundaries

This work requires no new Aevatar runtime path and no NyxID key contract
change. It consumes:

- canonical Studio Team/member/binding identities;
- canonical Team member automation creation/deletion;
- owner-scoped schedule diagnostic reads;
- owner-scoped member run reads;
- NyxID's existing API-key inventory and exact-key lifecycle facts.

Aevatar remains the owner of schedule and credential lifecycle state. NyxID
remains the owner of Agent Key identity, grants, active state, and
`last_used_at`. The Ornn skill orchestrates observations; it becomes authority
for none of them.

## Done Criteria

The work is complete when:

- the skill passes baseline and forward behavior tests;
- the Ornn package passes local and live format validation;
- it is searchable and readable through Ornn's authenticated public catalog;
- an authenticated user can run one wall-clock cron canary without
  `run-now`;
- the run produces the unique marker;
- the exact constrained Agent Key's `last_used_at` changes;
- the owner-scoped fire record reports `manual=false`;
- all temporary resources reach their canonical cleanup states;
- no raw key, bearer, Vault reference, or credential material appears in
  package files, agent-authored checkpoint/final output, or retained evidence;
- the skill never copies a complete `nyxid_api_keys` inventory into its own
  messages or evidence. The existing platform-managed tool result may contain
  non-secret key metadata and remains governed by the host's tool-trace
  retention policy.
