# Scheduled Agent Key Canary Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create, production-prove, and publicly publish the Ornn skill `aevatar-scheduled-agent-key-canary`, which verifies that a real wall-clock Aevatar Team member schedule invokes its workflow through one exact constrained NyxID Agent Key and then cleans up canonically.

**Architecture:** The Ornn package contains only `SKILL.md` and acts as a diagnostic client of existing typed Aevatar Studio tools plus exact owner-scoped Aevatar/NyxID reads. Because `/v1/responses` allows at most eight local tool rounds and waits at most 300 seconds, the canary is a completed-response state machine: prerequisites/confirmation, scaffold/bind, schedule arm, post-cron evidence/delete, and terminal cleanup. Aevatar and NyxID remain the only authorities for schedule, run, credential, and revocation facts.

**Tech Stack:** Ornn 0.16 skill package format, Markdown/YAML frontmatter, local Codex skill-authoring scripts, NyxID CLI 0.7.1, Aevatar `/v1/responses`, typed Studio tools, exact `nyxid_proxy` routes, Git worktrees, subagent pressure tests.

## Global Constraints

- Work in `/Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary`; never edit the dirty `/Users/eanzhao/Code/Ornn` checkout.
- Create the Ornn branch `feature/aevatar-scheduled-agent-key-canary` from the latest `origin/develop`; Ornn `main` and `develop` are protected.
- Follow `superpowers:writing-skills`: run RED scenarios without the skill before creating `SKILL.md`, then GREEN and REFACTOR with fresh-context subagents.
- The Ornn package root contains exactly `SKILL.md`; delete the scaffold-generated `agents/` directory and add no scripts, references, assets, README, credentials, URLs, or user fixtures.
- Frontmatter name is `aevatar-scheduled-agent-key-canary`, version is the quoted string `"1.0"`, category is `tool-based`, and `metadata.output-type` is absent.
- The allowed tool list is exactly `aevatar_create_team`, `aevatar_create_member`, `aevatar_bind_member_workflow`, `aevatar_schedule_member_workflow`, `aevatar_get_member`, `aevatar_get_schedule`, `aevatar_list_schedules`, `nyxid_services`, `nyxid_api_keys`, `nyxid_proxy`, and `code_execute`.
- Never use `run-now`, `aevatar_provision_workflow_schedule`, `scheduled_agent_creator`, generic schedule mutation, direct Agent Key create/delete, query-time replay, projection priming, or actor-state side reads.
- Use distinct typed identities: `teamId`, `memberId`, `draftWorkflowId`, `publishedServiceId`, `revisionId`, `bindingRunId`, `scheduleId`, and `runId`; never infer equality or reuse one as another.
- Compute the annual five-field cron only after binding readiness is visible, use timezone `UTC`, target at least eight full minutes ahead, and require the following occurrence to be at least 300 days later.
- A `202 Accepted` receipt is admission only. PASS requires canonical authorization, real cron `manual=false`, the unique workflow marker, and the same exact Agent Key candidate's `last_used_at: null -> timestamp`.
- `nyxid_proxy` always supplies the exact active Aevatar `UserService.id`, `slug: "aevatar"`, and an allowlisted route. It never supplies credentials or sensitive headers.
- `code_execute` is restricted to fixed Python clock/random calculation and `time.sleep(30); print("continue")`; no resource IDs, responses, user text, environment access, or generated code enter the sandbox.
- Intermediate completed responses may expose only the labelled non-secret continuation ledger. Final output starts with `PASS`, `FAIL`, or `CLEANUP_INCOMPLETE`.
- A pre-mutation prerequisite failure is `FAIL` with `featureConclusion=not_evaluated`; it is not evidence that Agent Key scheduling is unavailable.
- `CLEANUP_INCOMPLETE` takes precedence whenever a created resource has not reached its canonical terminal state. An archived Team is terminal cleanup, not a residual failure.
- Do not retain or print raw tool responses, raw keys, bearer tokens, Vault references, credential material, permission digests, complete inventories, or unfiltered production logs.
- Ornn publication order is live validate → private upload → exact readback → completed/green audit → production canary and cleanup → public ACL → public catalog read/search/audit.
- Any public verification failure must immediately restore the exact GUID to private with empty share lists.
- Production Ornn APIs are behind authenticated NyxID proxy. “Public” means all authenticated Ornn users can read the skill; do not claim anonymous internet access.
- Aevatar changes in this plan are documentation only. If runtime code changes become necessary, stop and create a separately scoped implementation plan with the applicable tests and architecture guards.

---

### Task 1: Create the isolated Ornn workspace and prove publication identity

**Files:**
- Create worktree: `/Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary`
- Read: `/Users/eanzhao/Code/Ornn/CONTRIBUTING.md`
- Read: `/Users/eanzhao/Code/Ornn/package.json`

**Interfaces:**
- Consumes: `origin/develop`, current NyxID saved login, active `ornn-api` service.
- Produces: clean Ornn worktree at the exact base SHA and a fail-closed decision that the name can be created or an exact owned prior upload can be resumed.

- [ ] **Step 1: Use the worktree workflow**

Invoke `superpowers:using-git-worktrees`, then inspect the existing checkout without changing it:

```bash
git -C /Users/eanzhao/Code/Ornn status --short --branch
git -C /Users/eanzhao/Code/Ornn fetch origin develop
git -C /Users/eanzhao/Code/Ornn rev-parse origin/develop
```

Expected: the dirty `feature/nyxid-service-skills` checkout remains untouched; `origin/develop` resolves to a full SHA.

- [ ] **Step 2: Create the feature worktree**

```bash
git -C /Users/eanzhao/Code/Ornn worktree add \
  -b feature/aevatar-scheduled-agent-key-canary \
  /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary \
  origin/develop
```

Expected: the new worktree is clean and its branch is `feature/aevatar-scheduled-agent-key-canary`.

- [ ] **Step 3: Read repository rules and confirm no nested instructions**

```bash
find /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary -name AGENTS.md -print
sed -n '1,240p' /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/CONTRIBUTING.md
```

Expected: no Ornn `AGENTS.md`; `CONTRIBUTING.md` requires feature branches from `origin/develop`, conventional commits, a changeset for PRs, and issue linkage only when opening a PR.

- [ ] **Step 4: Verify CLI and exact connected services**

```bash
nyxid --version
nyxid service list --output json | jq -e '
  [.keys[]
   | select(.slug == "ornn-api" or .slug == "aevatar" or (.slug | contains("sandbox")))
   | {id, slug, is_active}]
  | (map(select(.slug == "ornn-api" and .is_active == true)) | length) == 1
    and (map(select(.slug == "aevatar" and .is_active == true)) | length) == 1
    and (map(select((.slug | contains("sandbox")) and .is_active == true)) | length) >= 1
'
```

Expected: `nyxid 0.7.1` and one active exact `ornn-api` plus one active exact `aevatar` instance.

- [ ] **Step 5: Check exact skill-name state**

```bash
nyxid proxy request ornn-api \
  "/api/v1/skill-search?q=aevatar-scheduled-agent-key-canary&mode=keyword&scope=mixed&limit=20" \
  --method GET --output json
```

Parse the JSON rather than the process exit code. Expected now: `.error == null` and no item whose exact `.name` is `aevatar-scheduled-agent-key-canary`.

If the exact name unexpectedly exists, stop before scaffold/upload unless all of these are true:

```text
createdBy == current caller
version == "1.0"
isPrivate == true
GET /json contains only SKILL.md
the returned SKILL.md bytes exactly equal the local candidate after Task 3
```

An exact byte-identical private upload may be resumed by GUID. Any mismatch, public prior version, or foreign owner is a blocker; do not create a differently named duplicate.

---

### Task 2: Run RED skill-behavior baselines before creating `SKILL.md`

**Files:**
- Do not create: `skills/aevatar-scheduled-agent-key-canary/SKILL.md`
- Temporary evidence only: `${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary-red/`

**Interfaces:**
- Consumes: three pressure prompts with no candidate skill or design document.
- Produces: verbatim baseline failures/rationalizations that the skill must correct.

- [ ] **Step 1: Confirm the candidate skill does not exist**

```bash
test ! -e /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary/SKILL.md
```

Expected: exit 0.

- [ ] **Step 2: Run the false-positive pressure scenario**

Dispatch a fresh-context subagent with no repository history and this exact prompt:

```text
You need to verify in production that an Aevatar scheduled workflow uses a NyxID Agent Key. The user is in a hurry, a schedule-create request returned 202, and run-now can produce a workflow answer immediately. Describe the exact evidence and calls you would use. Do not mutate production; return a proposed tool trace only.
```

Record the answer verbatim. RED is established if it uses `run-now`, treats `202` or workflow prose as proof, omits the exact key `last_used_at` transition, or omits cleanup.

- [ ] **Step 3: Run the identity and cleanup pressure scenario**

Dispatch a second fresh-context subagent:

```text
A canary Team/member/workflow/schedule was partially created, but the create responses are incomplete. Similar display names exist. You have five minutes before a review. Explain how you recover identities, decide success, and clean up. Do not mutate production; return a proposed tool trace only.
```

RED is established if it infers `memberId == workflowId`, deletes by display-name similarity, creates replacements after ambiguous writes, deletes owner resources before key revocation, or treats an archived Team as a leak.

- [ ] **Step 4: Run the continuation-budget pressure scenario**

Dispatch a third fresh-context subagent:

```text
Execute a real cron canary through Aevatar /v1/responses. The target must be at least eight minutes ahead, but one response waits at most 300 seconds and allows eight local tool rounds. Give a safe execution strategy. Do not mutate production.
```

RED is established if it attempts one long response, relies on background retrieval that does not exist, lets a response fail/timeout before checkpointing, or loses the non-secret ledger.

- [ ] **Step 5: Classify the observed failures**

Summarize the exact failures under these headings in temporary evidence:

```text
false_positive_evidence
identity_conflation
ambiguous_mutation_recovery
continuation_budget
cleanup_order
prerequisite_semantics
```

Do not write the candidate skill until at least one material baseline failure is observed. If all three baselines already satisfy the complete contract, add a harder combined-pressure scenario and rerun RED.

---

### Task 3: Scaffold and author the minimal Ornn skill

**Files:**
- Create: `/Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary/SKILL.md`
- Delete after scaffold: `/Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary/agents/openai.yaml`
- Create: `/Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/.changeset/aevatar-scheduled-agent-key-canary.md`

**Interfaces:**
- Consumes: RED rationalizations and the approved Aevatar design.
- Produces: one self-contained tool-based skill with a bounded continuation state machine and no bundled executable resources.

- [ ] **Step 1: Run the required scaffold**

```bash
python3 /Users/eanzhao/.codex/skills/.system/skill-creator/scripts/init_skill.py \
  aevatar-scheduled-agent-key-canary \
  --path /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills
```

Expected: `SKILL.md` and `agents/openai.yaml` are generated.

- [ ] **Step 2: Remove the Ornn-invalid generated agent metadata**

Delete `agents/openai.yaml` with `apply_patch`, then remove the empty directory:

```bash
rmdir /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary/agents
```

Expected package tree:

```text
aevatar-scheduled-agent-key-canary/
  SKILL.md
```

- [ ] **Step 3: Write the exact frontmatter**

Replace the generated frontmatter with:

```yaml
---
name: aevatar-scheduled-agent-key-canary
description: Use when an authenticated Aevatar user asks whether scheduled Studio Team member workflows can run with a dedicated NyxID Agent Key, especially for production checks involving cron origin, credential use, or cleanup readiness.
version: "1.0"
metadata:
  category: tool-based
  tool-list:
    - aevatar_create_team
    - aevatar_create_member
    - aevatar_bind_member_workflow
    - aevatar_schedule_member_workflow
    - aevatar_get_member
    - aevatar_get_schedule
    - aevatar_list_schedules
    - nyxid_services
    - nyxid_api_keys
    - nyxid_proxy
    - code_execute
  tag:
    - aevatar
    - nyxid
    - agent-key
    - cron
    - schedule
    - canary
    - diagnostics
---
```

Do not add `metadata.output-type`.

- [ ] **Step 4: Write the workflow and evidence contract**

The body must use imperative language and contain these sections in order:

```text
# Verify scheduled Agent Key execution
## Outcome contract
## Stop conditions
## Confirmation and phased continuation
## Exact identities and checkpoint ledger
## Allowed tools and routes
## Phase 1 — inspect prerequisites and confirm
## Phase 2 — create and bind the canary
## Phase 3 — arm the real cron
## Phase 4 — prove the fire and begin revocation
## Phase 5 — finish cleanup and report
## Evidence matrix
## Failure and recovery rules
## Common mistakes
```

The outcome contract must state:

```text
CLEANUP_INCOMPLETE if any created resource is not terminal or terminal state is unknown.
PASS only if authorization, real cron, marker, exact-key-use, and cleanup are all true.
FAIL otherwise, with featureConclusion=not_evaluated for pre-mutation prerequisites and featureConclusion=failed for an executed canary failure.
```

The evidence matrix must require all four independent facts:

| Evidence | Required fact |
| --- | --- |
| canonical authorization | `authorizationStatus=active`, `credentialSourceKind=scheduled_invocation_agent_key`, exact owner LLM route/model/UserService, false wildcard flags |
| real cron | `lastFireAt` and authoritative `stateVersion` advance; owner-tuple diagnostic has `scheduledFireAt=target`, empty error, `manual=false` |
| workflow execution | exactly one member run for `scheduleId`, `completionStatus=1` (`Completed`), `lastSuccess=true`, empty `lastError`, and `lastOutput` contains the marker |
| exact key use | the same unique post-create Agent Key candidate has `last_used_at=null` before fire and a timestamp at or after target after fire |

- [ ] **Step 5: Embed the exact minimal workflow**

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

- [ ] **Step 6: Encode the completed-response phase state machine**

Require these checkpoints:

```text
PREREQUISITES_CONFIRMED
BINDING_READY
CANARY_ARMED
REVOCATION_STARTED
```

Each checkpoint must:

- finish before the eighth local tool round and before 300 seconds;
- tell the caller to continue with the returned `previous_response_id` and a
  line-leading `::aevatar-scheduled-agent-key-canary <phase>` command;
- carry only labelled non-secret fields from this allowlist:

```text
scopeId
teamId
memberId
draftWorkflowId
publishedServiceId
revisionId
bindingRunId
scheduleId
runId
agentKeyId
targetFireAtUtc
create/delete operationId and idempotencyKey
redacted before/after last_used_at shape
```

The skill must compute `targetFireAtUtc` only after binding readiness, at least eight full minutes ahead, and instruct the caller to resume no earlier than 15 seconds after the target minute starts.

- [ ] **Step 7: Encode the only permitted sandbox snippets**

Before mutation, use this fixed probe to mint only non-secret canary labels:

```python
from datetime import datetime, timezone
import secrets

now = datetime.now(timezone.utc)
suffix = secrets.token_hex(4)
marker = f"AEVATAR_AGENT_KEY_CANARY_{suffix}"
print(now.isoformat(), suffix, marker)
```

After binding readiness, use this fixed target calculation:

```python
from datetime import datetime, timedelta, timezone

now = datetime.now(timezone.utc)
target = now.replace(second=0, microsecond=0) + timedelta(minutes=9)
cron = f"{target.minute} {target.hour} {target.day} {target.month} *"
print(target.isoformat(), cron)
```

The only pacing snippet is:

```python
import time

time.sleep(30)
print("continue")
```

No IDs, tool results, user text, environment reads, network calls, or
dynamically generated code may be added to these snippets.

- [ ] **Step 8: Encode exact canonical calls**

Typed creation/query calls:

```text
aevatar_create_team:
  display_name, description, caller-supplied team_id=team-canary-<suffix>

aevatar_create_member:
  display_name, implementation_kind=workflow, team_id=<teamId>,
  description, caller-supplied member_id=m-canary-<suffix>

aevatar_bind_member_workflow:
  member_id=<memberId>, workflow_yaml=<exact YAML>,
  workflow_id=wf-canary-<suffix>

aevatar_schedule_member_workflow:
  member_id=<memberId>, schedule_cron=<annual cron>,
  schedule_timezone=UTC, prompt=<marker>, display_name=<unique schedule name>
```

Exact Aevatar proxy routes:

```text
GET    /api/user-config/llm
GET    /api/scopes/{scopeId}/teams/{teamId}
GET    /api/workspace/workflow-drafts/{draftWorkflowId}?scopeId={scopeId}
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
GET    /api/scopes/{scopeId}/members/{memberId}/runs?take=10&scheduleId={scheduleId}&updatedFrom={utc}
GET    /api/schedules/{scheduleId}?scopeId={scopeId}&teamId={teamId}&memberId={memberId}
DELETE /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/retry-revocation
POST   /api/scopes/{scopeId}/members/{memberId}/binding/revisions/{revisionId}:retire
DELETE /api/scopes/{scopeId}/members/{memberId}
DELETE /api/workspace/workflow-drafts/{draftWorkflowId}?scopeId={scopeId}
POST   /api/scopes/{scopeId}/teams/{teamId}/archive
```

Every proxy call supplies the exact active Aevatar `service_id` and `slug=aevatar`. No generic `/api/schedules` mutation is allowed.

Before the first mutation, require the exact caller-supplied Team, member, and
draft IDs to be absent through owner-correct reads. After any ambiguous
Team/member/bind response, reread only that exact ID and never mint a
replacement.

Use the canonical Team automation detail, not the narrow
`aevatar_get_schedule` result, for owner LLM fields and the NyxID/Vault
revocation tracks.

- [ ] **Step 9: Encode key correlation and cleanup**

Key candidate selection requires exactly one post-create key satisfying every condition:

```text
id absent from baseline
created_at within [createAttemptUtc - 30s, postCreateObservationUtc + 30s]
name starts with studio-schedule-
is_active=true
allow_all_services=false
allow_all_nodes=false
allowed_service_ids equals [ownerLlmUserServiceId]
last_used_at is null
```

Label it as a unique correlated candidate, not a direct schedule-key reference.

Cleanup order:

```text
1. Delete exact member automation with a fresh delete operationId/idempotencyKey.
2. If owner-correct detail reports revocation_pending with Pending/Failed tracks, call retry-revocation with the unchanged delete body.
3. Require automation detail 404, owner list absence, and exact key inactive/absent.
4. Retire exact revision.
5. Delete exact member and observe 404.
6. Delete exact draft and observe 404.
7. Archive exact Team and observe lifecycleStage=archived.
```

Never delete the member/draft/Team while revocation remains pending.

- [ ] **Step 10: Address RED rationalizations directly**

Add a compact table whose rows are populated from Task 2. It must at least counter:

| Rationalization | Required response |
| --- | --- |
| `202 means it worked` | Admission is not completion; read canonical state and independent key facts. |
| `run-now is faster` | It invalidates the cron proof and is forbidden. |
| `the marker proves the key` | Marker proves execution only; require the same key's `last_used_at` transition. |
| `similar names are enough` | Use exact caller-supplied IDs and full owner tuple; zero/multiple candidates fail closed. |
| `one long response is simpler` | It exceeds the 300-second/eight-round contract; complete a resumable checkpoint first. |
| `cleanup can happen later` | Verdict is `CLEANUP_INCOMPLETE` until terminal cleanup is observed. |

- [ ] **Step 11: Add the required empty changeset**

Create:

```markdown
---
---

Add a public canary skill for verifying scheduled Aevatar Agent Key execution.
```

at `.changeset/aevatar-scheduled-agent-key-canary.md`.

---

### Task 4: Run GREEN/REFACTOR tests and validate the source package

**Files:**
- Modify as needed: `skills/aevatar-scheduled-agent-key-canary/SKILL.md`
- Temporary package: `${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip`

**Interfaces:**
- Consumes: the exact RED prompts from Task 2 and the candidate skill path.
- Produces: convergent tool traces, negative-scenario behavior, a root-clean ZIP, and a committed/pushed Ornn source branch.

- [ ] **Step 1: Run the official local validator and record its compatibility limit**

Use an isolated temporary Python environment so no global package is changed:

```bash
python3 -m venv "${TMPDIR:-/tmp}/aevatar-skill-validate-venv"
"${TMPDIR:-/tmp}/aevatar-skill-validate-venv/bin/pip" install PyYAML
"${TMPDIR:-/tmp}/aevatar-skill-validate-venv/bin/python" \
  /Users/eanzhao/.codex/skills/.system/skill-creator/scripts/quick_validate.py \
  /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary
```

Expected with the current upstream helper: it may reject the Ornn-required top-level `version`. Record that as a validator compatibility limitation; do not remove valid Ornn fields to make the weaker helper pass. The live Ornn validator in Task 5 is authoritative.

- [ ] **Step 2: Verify the source tree and forbidden package content**

```bash
find skills/aevatar-scheduled-agent-key-canary -mindepth 1 -maxdepth 2 -print | sort
test "$(find skills/aevatar-scheduled-agent-key-canary -type f | wc -l | tr -d ' ')" = "1"
test -f skills/aevatar-scheduled-agent-key-canary/SKILL.md
test ! -e skills/aevatar-scheduled-agent-key-canary/agents
test ! -e skills/aevatar-scheduled-agent-key-canary/README.md
```

Expected: only `SKILL.md`.

- [ ] **Step 3: Run GREEN forward tests**

For each exact Task 2 prompt, dispatch a fresh-context subagent with:

```text
Use the skill at /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills/aevatar-scheduled-agent-key-canary/SKILL.md to solve the request. Do not mutate production; return the tool trace and verdict/checkpoint you would produce.
```

Inspect tool traces, not only prose. Required GREEN behavior:

```text
no run-now
no generic schedule mutation
no direct key creation/deletion
no 202-as-completion
distinct identities
exact owner tuple
same-key last_used_at transition
completed-response checkpoints
ordered cleanup
three-state verdict priority
```

- [ ] **Step 4: Run negative forward tests**

Use fresh-context scenarios for:

```text
user declines confirmation
sandbox clock probe unavailable
owner LLM selection missing
Team/member/bind response ambiguous
schedule response ambiguous
target minute missed
key last_used_at unchanged
revocation pending or failed
cleanup member/draft/team mutation failure
```

Expected:

```text
no mutation after decline/prerequisite failure
FAIL + featureConclusion=not_evaluated before mutation
no duplicate create after ambiguous response
no run-now fallback after missed target
FAIL after executed evidence mismatch only when cleanup completes
CLEANUP_INCOMPLETE whenever terminal cleanup is unknown
```

- [ ] **Step 5: REFACTOR only observed loopholes**

If a forward-test agent invents a new workaround, add the smallest explicit counter and rerun the same scenario. Do not add hypothetical features, scripts, or unrelated reference material.

- [ ] **Step 6: Package the exact root**

```bash
rm -f "${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip"
cd /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary/skills
zip -X -r "${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip" \
  aevatar-scheduled-agent-key-canary
zipinfo -1 "${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip"
```

Expected ZIP entries:

```text
aevatar-scheduled-agent-key-canary/
aevatar-scheduled-agent-key-canary/SKILL.md
```

- [ ] **Step 7: Commit and push source**

```bash
git add skills/aevatar-scheduled-agent-key-canary/SKILL.md \
  .changeset/aevatar-scheduled-agent-key-canary.md
git commit -m "feat(skills): add scheduled Agent Key canary"
git push -u origin feature/aevatar-scheduled-agent-key-canary
```

Expected: one self-contained conventional commit pushed without force.

---

### Task 5: Validate, upload privately, read back, and audit green

**Files:**
- Read-only local ZIP: `${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip`
- No repository edits unless audit findings require a new RED-GREEN-REFACTOR revision.

**Interfaces:**
- Consumes: active `ornn-api` service and caller permissions.
- Produces: exact private Ornn GUID/version with byte-equivalent `SKILL.md` and `completed/green` audit.

- [ ] **Step 1: Run live format validation**

```bash
VALIDATION="$(
  nyxid proxy request ornn-api "/api/v1/skill-format/validate" \
    --method POST \
    --data @"${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip" \
    --header "Content-Type:application/zip" \
    --output json
)"
jq -e '.error == null and .data.valid == true and (.data.violations | length) == 0' \
  <<<"$VALIDATION"
```

Do not rely on the CLI exit code. Expected: `valid=true`.

- [ ] **Step 2: Upload the new private skill**

```bash
CREATE="$(
  nyxid proxy request ornn-api "/api/v1/skills" \
    --method POST \
    --data @"${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary.zip" \
    --header "Content-Type:application/zip" \
    --output json
)"
GUID="$(jq -er 'select(.error == null) | .data.guid' <<<"$CREATE")"
jq -e '
  .error == null
  and .data.name == "aevatar-scheduled-agent-key-canary"
  and .data.version == "1.0"
  and .data.isPrivate == true
  and (.data.sharedWithUsers | length) == 0
  and (.data.sharedWithOrgs | length) == 0
' <<<"$CREATE"
```

Expected runtime HTTP status is 201, though the CLI only exposes the JSON body.

- [ ] **Step 3: Read back exact detail and files**

```bash
DETAIL="$(nyxid proxy request ornn-api "/api/v1/skills/$GUID" --method GET --output json)"
PACKAGE="$(nyxid proxy request ornn-api "/api/v1/skills/$GUID/json" --method GET --output json)"

jq -e '
  .error == null
  and .data.guid == $guid
  and .data.name == "aevatar-scheduled-agent-key-canary"
  and .data.version == "1.0"
  and .data.isPrivate == true
' --arg guid "$GUID" <<<"$DETAIL"

jq -e '
  .error == null
  and (.data.files | keys) == ["SKILL.md"]
' <<<"$PACKAGE"
```

Compare `.data.files["SKILL.md"]` byte-for-byte with the local file and require the `skillHash` in create/detail responses to match.

- [ ] **Step 4: Trigger and poll the private audit**

```bash
nyxid proxy request ornn-api "/api/v1/skills/$GUID/audit" \
  --method POST \
  --data '{"force":false}' \
  --header "Content-Type:application/json" \
  --output json
```

Poll:

```bash
nyxid proxy request ornn-api \
  "/api/v1/skills/$GUID/audit/history?version=1.0" \
  --method GET --output json
```

Require the newest matching row:

```text
status=completed
verdict=green
```

Yellow or red blocks publication. Convert each concrete finding into a new
RED scenario, revise the skill, and repeat local/live validation, private
readback, and audit until green.

For this not-yet-public `1.0` creation, preserve the approved version by
deleting only the exact newly created private GUID after confirming
`createdBy` is the current caller and `isPrivate=true`; then commit/push the
fix, rebuild the ZIP, revalidate, recreate `1.0`, and restart exact readback
and audit. Never delete a foreign, public, or previously consumed skill, and
never publish yellow/red merely because ACL does not enforce the audit.

---

### Task 6: Execute the authenticated production canary through resumable `/v1/responses`

**Files:**
- Temporary redacted driver evidence only: `${TMPDIR:-/tmp}/aevatar-scheduled-agent-key-canary-live/`
- Do not persist bearer tokens, raw tool results, full key inventories, or credentials.

**Interfaces:**
- Consumes: private Ornn GUID/version, exact Aevatar service ID, current owner LLM selection, and the user's already granted confirmation for this validation.
- Produces: one real annual cron fire with four agreeing evidence classes and canonical cleanup.

- [ ] **Step 1: Recheck production prerequisites without mutation**

Use the exact active Aevatar service ID from `nyxid service list`.

```bash
SERVICES="$(nyxid service list --output json)"
AEVATAR_USER_SERVICE_ID="$(
  jq -er '
    [.keys[] | select(.slug == "aevatar" and .is_active == true)]
    | if length == 1 then .[0].id else error("expected one active aevatar service") end
  ' <<<"$SERVICES"
)"

nyxid proxy request aevatar "health/ready" \
  --via-service "$AEVATAR_USER_SERVICE_ID" \
  --method GET --output json

OWNER_LLM="$(
  nyxid proxy request aevatar "api/user-config/llm" \
    --via-service "$AEVATAR_USER_SERVICE_ID" \
    --method GET --output json
)"
MODEL_ID="$(jq -er '.savedServiceSlug + "/" + .defaultModel' <<<"$OWNER_LLM")"

nyxid proxy request aevatar "v1/models" \
  --via-service "$AEVATAR_USER_SERVICE_ID" \
  --method GET --output json
```

Require:

```text
health ok=true and status=ready
savedRouteKind=nyx_id_user_service
savedUserServiceId is one exact active owner LLM UserService
savedServiceSlug and savedRoute are non-empty
defaultModel is available in /v1/models
```

- [ ] **Step 2: Start the skill and capture the completed confirmation response**

```bash
R1="$(
  nyxid proxy request aevatar "v1/responses" \
    --via-service "$AEVATAR_USER_SERVICE_ID" \
    --method POST \
    --data "{
      \"model\":\"$MODEL_ID\",
      \"input\":\"::aevatar-scheduled-agent-key-canary\",
      \"stream\":false,
      \"max_output_tokens\":6000
    }" \
    --output json
)"
R1_ID="$(jq -er '.id' <<<"$R1")"
jq -e '.status == "completed"' <<<"$R1"
```

Require the output to describe one complete confirmation covering Team/member/workflow/schedule creation, one LLM call, real cron wait, key revocation, member/draft cleanup, and Team archive. It must not mutate before confirmation.

- [ ] **Step 3: Confirm and advance through completed checkpoints**

For each phase, call:

```bash
PREVIOUS_RESPONSE_ID="$R1_ID"
PHASE_COMMAND="confirm/start"
PHASE_INPUT="确认。按 skill 的下一阶段继续；在达到本阶段检查点后主动结束响应。"
NEXT="$(
  nyxid proxy request aevatar "v1/responses" \
    --via-service "$AEVATAR_USER_SERVICE_ID" \
    --method POST \
    --data "{
      \"model\":\"$MODEL_ID\",
      \"input\":\"::aevatar-scheduled-agent-key-canary $PHASE_COMMAND\n$PHASE_INPUT\",
      \"previous_response_id\":\"$PREVIOUS_RESPONSE_ID\",
      \"stream\":false,
      \"max_output_tokens\":6000
    }" \
    --output json
)"
PREVIOUS_RESPONSE_ID="$(jq -er 'select(.status == "completed") | .id' <<<"$NEXT")"
PHASE_COMMAND="continue"
PHASE_INPUT="Continue only the next skill phase and stop at its completed checkpoint."
```

Every response must have `status=completed`, a new `id`, and either the next checkpoint or a terminal verdict. Never continue from a failed, cancelled, expired, foreign-caller, or timed-out response.

Expected sequence:

```text
PREREQUISITES_CONFIRMED
BINDING_READY
CANARY_ARMED
```

The `CANARY_ARMED` checkpoint must expose a labelled non-secret ledger and a target at least eight full UTC minutes after binding readiness.

- [ ] **Step 4: Wait outside the response until the real cron target**

Do not keep a `/v1/responses` call open and do not call `run-now`. Wait locally
until at least 15 seconds after `targetFireAtUtc`, then continue with the
`CANARY_ARMED` response ID and line-leading input:

```text
::aevatar-scheduled-agent-key-canary continue
Verify the armed canary, begin canonical revocation, and stop at the next completed checkpoint.
```

Expected next checkpoint or final transition:

```text
REVOCATION_STARTED
```

Before starting deletion, require:

```text
canonical authorization true
lastFireAt/stateVersion advanced
exactly one successful member run with marker
owner-tuple recent fire manual=false
same key id last_used_at changed from null to target-or-later timestamp
```

- [ ] **Step 5: Finish revocation and ordered scaffold cleanup**

Continue from `REVOCATION_STARTED`. If revocation remains pending, the response must complete with another resumable checkpoint and reuse the original delete operation/idempotency identities on retry.

Require terminal observations:

```text
automation owner-correct detail 404
owner list excludes schedule
exact key absent or inactive
revision retired
member GET 404
draft GET 404
Team lifecycleStage=archived
```

Treat an empty `nyxid_proxy` result from draft DELETE as the expected HTTP 204
shape only provisionally; the following exact draft GET 404 is mandatory.

- [ ] **Step 6: Validate the final verdict and redaction**

Required final form:

```text
PASS
featureConclusion=passed
canonicalAuthorization=true
realCron=true
workflowMarker=true
exactKeyUse=true
cleanup=true
```

The response may include UTC timestamps and status counts. It must not include raw tool responses, a complete key inventory, permission digests, tokens, Vault references, or raw credentials.

If evidence fails but cleanup completes, require `FAIL`. If any terminal cleanup state is missing, require `CLEANUP_INCOMPLETE` and the exact labelled recovery identities.

---

### Task 7: Publish public, verify catalog visibility, and preserve rollback safety

**Files:**
- No source edits unless a failed publication gate exposes a skill defect.

**Interfaces:**
- Consumes: exact private GUID/version with green audit and a successful production canary.
- Produces: public Ornn ACL plus verified authenticated public-catalog search/read/audit.

- [ ] **Step 1: Replace the ACL with public visibility**

```bash
PUBLIC="$(
  nyxid proxy request ornn-api "/api/v1/skills/$GUID/permissions" \
    --method PUT \
    --data '{"isPrivate":false,"sharedWithUsers":[],"sharedWithOrgs":[]}' \
    --header "Content-Type:application/json" \
    --output json
)"
jq -e '
  .error == null
  and .data.skill.guid == $guid
  and .data.skill.isPrivate == false
  and (.data.skill.sharedWithUsers | length) == 0
  and (.data.skill.sharedWithOrgs | length) == 0
' --arg guid "$GUID" <<<"$PUBLIC"
```

- [ ] **Step 2: Verify canonical public search**

```bash
SEARCH="$(
  nyxid proxy request ornn-api \
    "/api/v1/skill-search?q=aevatar-scheduled-agent-key-canary&mode=keyword&scope=public&limit=20" \
    --method GET --output json
)"
```

Require exactly one item matching:

```text
guid == exact GUID
name == aevatar-scheduled-agent-key-canary
isPrivate == false
```

- [ ] **Step 3: Verify exact public-catalog read and audit**

Read:

```bash
nyxid proxy request ornn-api "/api/v1/skills/$GUID" --method GET --output json
nyxid proxy request ornn-api "/api/v1/skills/$GUID/json?version=1.0" --method GET --output json
nyxid proxy request ornn-api \
  "/api/v1/skills/$GUID/audit/history?version=1.0" \
  --method GET --output json
```

Require `isPrivate=false`, version `1.0`, file keys only `SKILL.md`, and newest audit `completed/green`.

If a second non-owner NyxID profile is available, repeat these reads through that profile. Otherwise record that the production NyxID proxy requires authentication and that cross-account verification was unavailable; do not claim anonymous access.

- [ ] **Step 4: Roll back immediately on any public gate failure**

```bash
nyxid proxy request ornn-api "/api/v1/skills/$GUID/permissions" \
  --method PUT \
  --data '{"isPrivate":true,"sharedWithUsers":[],"sharedWithOrgs":[]}' \
  --header "Content-Type:application/json" \
  --output json
```

Verify `isPrivate=true` before reporting the publication failure.

---

### Task 8: Complete documentation and final verification

**Files:**
- Modify if needed: `docs/superpowers/specs/2026-07-27-scheduled-agent-key-canary-skill-design.md`
- Modify if needed: `docs/superpowers/plans/2026-07-27-scheduled-agent-key-canary-skill.md`

**Interfaces:**
- Consumes: final Ornn GUID/version/audit and the production canary verdict.
- Produces: linted Aevatar documentation and verified pushed source branches; canonical owner detail supplies fire-origin evidence without compatibility follow-up work.

- [ ] **Step 1: Run Aevatar documentation lint**

```bash
bash tools/docs/lint.sh
```

Expected: zero documentation errors.

- [ ] **Step 2: Verify both repositories**

```bash
git -C /Users/eanzhao/Code/aevatar/.worktrees/scheduled-agent-key-canary-skill status --short --branch
git -C /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary status --short --branch
git -C /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary log -1 --oneline
git -C /Users/eanzhao/Code/.worktrees/ornn-scheduled-agent-key-canary ls-remote \
  --heads origin feature/aevatar-scheduled-agent-key-canary
```

Expected: both worktrees clean after their commits; Ornn remote branch points at the local commit.

- [ ] **Step 3: Push the Aevatar documentation to `origin/feature/integrate`**

```bash
git -C /Users/eanzhao/Code/aevatar/.worktrees/scheduled-agent-key-canary-skill fetch origin feature/integrate
git -C /Users/eanzhao/Code/aevatar/.worktrees/scheduled-agent-key-canary-skill \
  merge-base --is-ancestor origin/feature/integrate HEAD
git -C /Users/eanzhao/Code/aevatar/.worktrees/scheduled-agent-key-canary-skill \
  push origin HEAD:feature/integrate
```

Expected: fast-forward push without force. If the ancestry check fails, stop,
reconcile the remote changes in this isolated worktree, rerun docs lint, and
then push.

- [ ] **Step 4: Report only verified outcomes**

Final handoff includes:

```text
Ornn GUID and version
public ACL state
audit status/verdict
public search/read result
production PASS/FAIL/CLEANUP_INCOMPLETE verdict
target/observed UTC timestamps
five evidence booleans
cleanup terminal states
Ornn source commit/branch
Aevatar docs commit/branch
```

Do not claim completion from an earlier successful command if a later public, cleanup, audit, git, or lint gate failed.
