# Ornn Aevatar Platform Scheduled Agent Key Skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:executing-plans` to implement this plan task-by-task. Use
> `superpowers:writing-skills`, `skill-creator`,
> `superpowers:test-driven-development`, and
> `superpowers:verification-before-completion` for each skill. Fresh-context
> agents are evaluation subjects only; do not delegate implementation.

**Goal:** Publish five corrected Aevatar scheduling-related Ornn skill
versions and one `aevatar-platform` skillset revision that model Team member
automation, independent scheduled skill agents, and generic schedules as
distinct resources with their real credential lifecycles.

**Architecture:** Recover each immutable remote package into a clean Ornn
worktree, then upgrade one skill at a time through RED, GREEN, REFACTOR, live
format validation, stable-GUID publication, and exact-version download
verification. Only after all five skills are verified, publish a new skillset
revision from a checked-in manifest and master prompt. The Aevatar runtime and
production scheduled resources remain read-only throughout.

**Tech Stack:** Ornn `SKILL.md` packages, NyxID CLI proxy, Ornn REST API,
ZIP/SHA-256 tooling, Ruby/YAML for local structure checks, Ornn's live Zod
validator, Git worktrees, fresh-context skill evaluations.

## Global Constraints

- Implement from a new Ornn worktree and branch based on fresh
  `origin/develop`; preserve the dirty checkout at `/Users/eanzhao/Code/Ornn`.
- Use branch `feature/aevatar-scheduled-agent-key-skills` and external worktree
  `/Users/eanzhao/Code/.worktrees/ornn-aevatar-scheduled-agent-key-skills`.
- The only remote mutations permitted are the five existing Ornn skill GUIDs
  and the existing `aevatar-platform` skillset GUID.
- Do not mutate Aevatar runtime code, NyxID configuration, a Team, a member, a
  workflow, a schedule, a run, an Agent Key, a Vault secret, or a canary.
- Do not add `aevatar-scheduled-agent-key-canary` to this release.
- Use the exact connected Ornn UserService ID
  `919208f4-d5f3-4840-8eba-8643820ce7f2`; never substitute the catalog service
  ID `27dc402b-c7f1-4db3-a840-e13b7956b6a7`.
- Keep `memberId`, draft `workflowId`, `publishedServiceId`, UserService ID,
  catalog service ID, schedule ID, and Agent Key ID semantically distinct.
- Team member automation is owned by
  `/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations` and is
  created in-session with `aevatar_schedule_member_workflow`.
- Independent scheduled Ornn skill agents and one-shot reminders use
  `scheduled_agent_creator`, then `agent_builder` for management.
- Generic platform service/envelope scheduling uses `/api/schedules`; a
  binding-exchange source may legitimately produce a short-lived bearer, but
  that model must not leak into the two Agent Key paths.
- Dedicated schedule keys use exact revalidated authorization, both wildcard
  flags false, raw material only in `ISecretVault`, typed durable references,
  per-operation late resolution, generations, and dual-track revocation.
- `202 Accepted` proves admission only. Credential health (`active`) and firing
  state (`enabled`) are separate.
- Pause/resume preserves the active key. Reauthorization creates a new
  generation and revokes the old generation after replacement commits.
- Delete remains visible while either NyxID or Vault revocation is pending and
  retry reuses the original delete operation identity.
- No artifact or response may contain a raw key, access/bearer/refresh token,
  Vault reference or ciphertext, actual permission digest, API-key inventory,
  or authorization header.
- Preserve the five stable skill GUIDs and the stable skillset GUID. Versions
  are immutable; never delete or overwrite rollback versions.
- Stop on the first failed per-skill gate. Do not update the skillset until all
  five target versions are published and downloaded back successfully.
- Use Ornn's live `/skill-format/validate` as the complete format authority.
  The local Ruby/YAML check covers structure, naming, quoted version, and
  plain-category metadata before that call. The generic `skill-creator` quick
  validator is not a gate because it rejects Ornn's required `version` field.
- Run skill TDD serially. Complete RED, GREEN, REFACTOR, package validation,
  publication, readback, and local commit for one skill before touching the
  next skill.
- Do not push the Git branch or create a PR without separate user authority.

---

## Fixed Identities and Versions

| Resource | Stable GUID | Old | Target |
| --- | --- | ---: | ---: |
| `aevatar-scheduler` | `8d4bb4e0-81e8-472b-bd71-2777130aba2f` | `1.7` | `1.8` |
| `aevatar-automation` | `1f8e2f07-67d3-4ac8-b7de-4596f36f4634` | `1.1` | `1.2` |
| `aevatar-channels-delivery` | `d2b575d3-0d80-4167-99e4-6161be47db7f` | `1.1` | `1.2` |
| `aevatar-triage` | `fbd40315-317f-4f80-9885-b44b83e1a204` | `1.3` | `1.4` |
| `aevatar-platform-map` | `b8bf9e98-2658-4e09-9c51-2e4958137091` | `1.7` | `1.8` |
| `aevatar-platform` | `248b99d6-36ff-4d41-bb45-baa25c6a9cad` | `1.11` | `1.12` |

Expected fixed old package hashes:

```text
aevatar-scheduler@1.7          8d56f22fbc7bb82cbdd9c1b777070ef7c14eaf3c19938b4002786380c246395a
aevatar-automation@1.1         0bb4f61391e185df608208b01708c1bf9c485d6621fedfdcbf3f09ee0594bad3
aevatar-channels-delivery@1.1  ce749f1d9459d6444d673ead42aade50bf061ce0e47b16dfc4259160e6c71d08
aevatar-triage@1.3             4ac7270d401e5245e9fb762d551120e5628436ef159d1cdc187b4ef4b0c3d388
aevatar-platform-map@1.7       10ec2384b9c200594d20a0483237e8d0f77b56f917e802fe38fed93b904ac683
```

## File Map

All implementation files below live in the isolated Ornn worktree.

- Create `skills/aevatar-scheduler/SKILL.md`: scheduling resource router and
  canonical Team automation operating guide, with generic scheduling isolated
  as an advanced path.
- Create `skills/aevatar-automation/SKILL.md`: independent scheduled skill
  agent/one-shot guide using exact typed NyxID service requirements and a
  dedicated Agent Key.
- Create `skills/aevatar-channels-delivery/SKILL.md`: channel/delivery guide
  with source-first credential diagnostics.
- Create `skills/aevatar-triage/SKILL.md`: three-layer diagnostic guide with
  typed schedule/credential evidence and honest evidence stages.
- Create `skills/aevatar-platform-map/SKILL.md`: product ownership map and
  routing entry point.
- Create
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`: RED and
  GREEN prompts, verbatim failure excerpts, pass/fail decisions, package
  hashes, validation results, and remote readback evidence. It must contain no
  secret material.
- Create `skillsets/aevatar-platform/manifest.json`: publish metadata and all
  twelve exact member pins; it intentionally has no owner-supplied version.
- Create `skillsets/aevatar-platform/master-prompt.md`: readable source for the
  versioned skillset instructions.
- Create `.changeset/calm-aevatar-agent-keys.md`: empty package changeset for
  repository delivery of content-only skills.

## Shared Release Functions

Use these shell variables in every task:

```bash
export ORNN_USER_SERVICE_ID='919208f4-d5f3-4840-8eba-8643820ce7f2'
export ORNN_WORKTREE='/Users/eanzhao/Code/.worktrees/ornn-aevatar-scheduled-agent-key-skills'
export ORNN_RELEASE_TMP='/tmp/ornn-aevatar-scheduled-agent-key-skills'
mkdir -p "$ORNN_RELEASE_TMP"
```

Build a candidate ZIP with one kebab-case root folder:

```bash
build_skill_zip() {
  local name="$1" version="$2"
  local zip_path="$ORNN_RELEASE_TMP/${name}-${version}.zip"
  rm -f "$zip_path"
  (
    cd "$ORNN_WORKTREE/skills"
    COPYFILE_DISABLE=1 zip -X -q -r "$zip_path" "$name"
  )
  unzip -Z1 "$zip_path"
  sha256sum "$zip_path"
}
```

Run a dependency-free local frontmatter structure check before the complete
live Ornn Zod validation:

```bash
validate_frontmatter() {
  local skill_dir="$1"
  ruby - "$skill_dir" <<'RUBY'
require "yaml"
path = File.join(ARGV.fetch(0), "SKILL.md")
text = File.read(path, encoding: "UTF-8")
match = text.match(/\A---\n(.*?)\n---/m) or abort("frontmatter missing")
frontmatter = YAML.safe_load(match[1], [], [], false)
abort("frontmatter must be a mapping") unless frontmatter.is_a?(Hash)
name = frontmatter["name"]
description = frontmatter["description"]
version = frontmatter["version"]
metadata = frontmatter["metadata"]
folder = File.basename(ARGV.fetch(0))
abort("invalid name") unless name.is_a?(String) && name.match?(/\A[a-z0-9][a-z0-9-]*\z/)
abort("folder/name mismatch") unless name == folder
abort("invalid description") unless description.is_a?(String) && !description.strip.empty? && description.length <= 1536
abort("invalid version") unless version.is_a?(String) && version.match?(/\A(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\z/)
abort("invalid metadata") unless metadata.is_a?(Hash) && metadata["category"] == "plain"
tags = metadata.fetch("tag", [])
abort("invalid tags") unless tags.is_a?(Array) && tags.all? { |tag| tag.is_a?(String) && tag.match?(/\A[a-z0-9-]+\z/) }
puts({name: name, version: version, category: metadata["category"], tags: tags}.inspect)
RUBY
}
```

Validate without publishing:

```bash
validate_live_zip() {
  local zip_path="$1"
  nyxid proxy request ornn-api '/api/v1/skill-format/validate' \
    --via-service "$ORNN_USER_SERVICE_ID" \
    --method POST \
    --header 'Content-Type:application/zip' \
    --data "@$zip_path" \
    --output json \
    | jq -e '.error == null and .data.valid == true and (.data.violations | length) == 0'
}
```

Publish one stable skill GUID and verify the response:

```bash
publish_skill_zip() {
  local guid="$1" version="$2" zip_path="$3"
  local candidate_sha
  candidate_sha=$(sha256sum "$zip_path" | awk '{print $1}')
  nyxid proxy request ornn-api "/api/v1/skills/$guid" \
    --via-service "$ORNN_USER_SERVICE_ID" \
    --method PUT \
    --header 'Content-Type:application/zip' \
    --data "@$zip_path" \
    --output json \
    | jq -e --arg guid "$guid" --arg version "$version" --arg sha "$candidate_sha" \
      '.error == null and .data.guid == $guid and .data.version == $version and .data.skillHash == $sha'
}
```

Download the exact immutable version and compare bytes:

```bash
verify_published_zip() {
  local guid="$1" name="$2" version="$3" candidate_zip="$4"
  local downloaded="$ORNN_RELEASE_TMP/${name}-${version}-published.zip"
  nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/versions/$version/download" \
    --via-service "$ORNN_USER_SERVICE_ID" \
    --stream > "$downloaded"
  cmp -s "$candidate_zip" "$downloaded"
  unzip -t "$downloaded"
  unzip -Z1 "$downloaded"
  sha256sum "$candidate_zip" "$downloaded"
}
```

If a publish call times out or returns an ambiguous transport error, do not
repeat it. First query the exact GUID and version list, then download the target
version. Treat it as published only when the target version exists and its
downloaded SHA-256 equals the local candidate SHA-256.

---

### Task 1: Create the Isolated Ornn Source and Evidence Baseline

**Files:**

- Create: `skills/aevatar-scheduler/SKILL.md`
- Create: `skills/aevatar-automation/SKILL.md`
- Create: `skills/aevatar-channels-delivery/SKILL.md`
- Create: `skills/aevatar-triage/SKILL.md`
- Create: `skills/aevatar-platform-map/SKILL.md`
- Create:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: immutable Ornn packages identified by the GUID/version/hash table.
- Produces: untouched old-version sources for RED tests and one evaluation log
  shared by later tasks.

- [ ] **Step 1: Create a clean worktree from current `origin/develop`**

```bash
cd /Users/eanzhao/Code/Ornn
git fetch origin develop
test "$(git rev-parse origin/develop)" = "$(git rev-parse refs/remotes/origin/develop)"
test ! -e /Users/eanzhao/Code/.worktrees/ornn-aevatar-scheduled-agent-key-skills
git worktree add \
  /Users/eanzhao/Code/.worktrees/ornn-aevatar-scheduled-agent-key-skills \
  -b feature/aevatar-scheduled-agent-key-skills \
  origin/develop
```

Expected: a new clean worktree on
`feature/aevatar-scheduled-agent-key-skills`; the original checkout retains its
four `nyxid-service-*` directories and existing changeset unchanged.

- [ ] **Step 2: Snapshot the remote identities before any write**

Run `GET /api/v1/skills/{guid}` for all five GUIDs and
`GET /api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad` through the exact
UserService. Assert the old versions, stable GUIDs, `isPrivate == false`,
skillset `version == "1.11"`, twelve members, and
`memberVisibilityState == "all-public"`.

- [ ] **Step 3: Download and verify all five immutable old packages**

For each row in the fixed table:

```bash
nyxid proxy request ornn-api \
  "/api/v1/skills/$guid/versions/$old_version/download" \
  --via-service "$ORNN_USER_SERVICE_ID" \
  --stream > "$ORNN_RELEASE_TMP/${name}-${old_version}.zip"
test "$(sha256sum "$ORNN_RELEASE_TMP/${name}-${old_version}.zip" | awk '{print $1}')" = "$expected_sha"
unzip -t "$ORNN_RELEASE_TMP/${name}-${old_version}.zip"
unzip -Z1 "$ORNN_RELEASE_TMP/${name}-${old_version}.zip"
```

Expected for each package: one root directory named exactly after the skill and
one `SKILL.md`; no unexpected package files.

- [ ] **Step 4: Recover the packages mechanically into `skills/`**

```bash
cd "$ORNN_WORKTREE"
for archive in \
  "$ORNN_RELEASE_TMP/aevatar-scheduler-1.7.zip" \
  "$ORNN_RELEASE_TMP/aevatar-automation-1.1.zip" \
  "$ORNN_RELEASE_TMP/aevatar-channels-delivery-1.1.zip" \
  "$ORNN_RELEASE_TMP/aevatar-triage-1.3.zip" \
  "$ORNN_RELEASE_TMP/aevatar-platform-map-1.7.zip"
do
  unzip -q "$archive" -d skills
done
git status --short
```

Expected: exactly five new skill directories. No file from the original dirty
checkout appears in this worktree.

- [ ] **Step 5: Create the evaluation record before changing a skill**

Create the validation document with these sections and table columns:

```markdown
# Aevatar Platform Scheduled Agent Key Skill Evaluation

## Safety Boundary

Only Ornn skill and skillset endpoints are mutated. Evaluation prompts use
synthetic identities (`m-alpha`, `wf-alpha`, `svc-alpha`) and contain no secret
material.

## Remote Baseline

| Resource | GUID | Old version | Old package SHA-256 | RED result |
| --- | --- | ---: | --- | --- |

## Per-Skill RED/GREEN Evidence

For every skill record: exact prompt, old-version response excerpt, observed
failure, candidate response excerpt, acceptance decision, local validation,
live validation, candidate SHA-256, and downloaded SHA-256.

## Skillset Evidence

Record the `1.11` RED result, `1.12` candidate result, exact member pins,
closure, visibility, and rollback-read verification.
```

- [ ] **Step 6: Verify the recovered baseline**

```bash
cd "$ORNN_WORKTREE"
git diff --check
find skills/aevatar-{scheduler,automation,channels-delivery,triage,platform-map} \
  -maxdepth 2 -type f -print | sort
```

Expected: no whitespace errors and exactly five `SKILL.md` source files.

- [ ] **Step 7: Commit the immutable source recovery**

```bash
git add \
  skills/aevatar-scheduler/SKILL.md \
  skills/aevatar-automation/SKILL.md \
  skills/aevatar-channels-delivery/SKILL.md \
  skills/aevatar-triage/SKILL.md \
  skills/aevatar-platform-map/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "chore(skills): recover Aevatar platform skill sources"
```

---

### Task 2: Upgrade `aevatar-scheduler` to 1.8

**Files:**

- Modify: `skills/aevatar-scheduler/SKILL.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: canonical Team automation REST/tool contracts and the three-resource
  decision table.
- Produces: the primary scheduling router used by the map and skillset prompt.

- [ ] **Step 1: Run the fixed 1.7 RED scenario in a fresh context**

Evaluation prompt:

```text
Read only skills/aevatar-scheduler/SKILL.md and act as the operating assistant.
The user already has scope scope-alpha, Team team-alpha, member m-alpha, draft
workflow wf-alpha, and published service svc-alpha; these IDs are all distinct.
They want that same bound member workflow every weekday at 09:00 Asia/Shanghai.
Explain the exact creation path and what must be checked after a 202 accepted
receipt. Then answer four follow-ups: (1) a new exact external service is added,
(2) pause then resume, (3) delete reports revocation pending, and (4) a run using
credentialSourceKind=scheduled_invocation_agent_key reports token_expired six
minutes after fire. Do not make real calls.
```

Expected RED: at least one of generic `/api/schedules`, `scopeOwnerNyxId`, a
shared 300-second fire token, identity conflation, premature completion,
credential recreation on resume, in-place grant expansion, or a fresh delete
operation. Record the exact failure excerpt. If none occurs, preserve the
correct old behavior and shape only the missing positive contract.

- [ ] **Step 2: Replace the frontmatter and scheduling decision contract**

Use quoted version `"1.8"`. The description must start with `Use when`, route
schedule/cron/recurring/run-now/pause/resume/reauthorize/delete intents, and say
that credential diagnosis depends on the chosen typed source. Keep the existing
lowercase tags.

The body must use this section order:

```markdown
# Operate Aevatar schedules

## Choose the scheduled resource first
## Canonical Team member automation
### Resolve the exact owner and identities
### Preflight without provisioning
### Confirm and create
### Reread authoritative state after admission
### Update, pause, resume, and run now
### Reauthorize a new credential generation
### Delete and retry durable revocation
### Diagnose credential and authorization failures
## Independent scheduled Ornn skill agents
## Advanced: generic platform schedules
## Safety and honesty checks
```

The first section must contain an explicit three-row decision table. The Team
row must choose `aevatar_schedule_member_workflow` in-session or the canonical
owner path over REST. The independent-agent row must hand off to
`aevatar-automation`. The generic row alone may use `/api/schedules`.

- [ ] **Step 3: Write the canonical Team automation flow**

Document these exact REST actions:

```text
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
PUT    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/pause
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/resume
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/run-now
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/reauthorize
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}/retry-revocation
DELETE /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
```

Use distinct fixtures `m-alpha`, `wf-alpha`, and `svc-alpha`. State that the
server derives `publishedServiceId`; no caller-provided workflow or service ID
may replace `memberId`.

The create guide must say to return the preflight's confirmed policy version
and permission digest opaquely, use
`credentialProvisioningKind = dedicated_scheduled_invocation_agent_key`, and
provide stable operation/idempotency identities. It must not print the digest.

- [ ] **Step 4: Write the Agent Key lifecycle and source-first diagnosis**

Use a positive contract:

```text
Team automation provisions a dedicated constrained Agent Key from an exact,
revalidated typed authorization plan. Both wildcard flags are false. Raw key
material is written only to ISecretVault; durable state exposes only typed
reference and non-secret lifecycle facts. Workflow operations borrow the
durable reference and late-resolve it through the Vault each time a caller
credential is requested.
```

Reread after `202 Accepted` and require a newer `stateVersion`. Explain
`authorizationStatus == active`,
`credentialSourceKind == scheduled_invocation_agent_key`, future expiry,
positive generation, `revocationPending == false`, and the independent
`enabled` dimension.

State the recovery rules exactly: changed plan means new preflight;
authorization drift means explicit reauthorize; pause/resume preserves the
key; reauthorize creates a replacement generation; delete retries the original
operation until both NyxID and Vault tracks complete; Agent Key failures do not
fall back to interactive credentials or wildcard authority.

- [ ] **Step 5: Retain generic scheduling only as an isolated advanced path**

Keep useful `/api/schedules` service/envelope and cron-preview guidance under
the advanced heading. Clearly label a NyxID binding-exchange credential as a
different typed source that may mint a short-lived bearer. Remove the global
five-minute design rule, Team use of generic scheduling, and CLI reading of
local token files.

- [ ] **Step 6: Run local and static checks**

```bash
cd "$ORNN_WORKTREE"
validate_frontmatter skills/aevatar-scheduler
test "$(sed -n 's/^version: "\([^"]*\)"/\1/p' skills/aevatar-scheduler/SKILL.md)" = '1.8'
rg -n 'm-alpha|wf-alpha|svc-alpha|aevatar_schedule_member_workflow|dedicated_scheduled_invocation_agent_key|retry-revocation' \
  skills/aevatar-scheduler/SKILL.md
if rg -n 'required_service_slugs|every scheduled run|one token.*whole run|scopeOwnerNyxId.*Team' \
  skills/aevatar-scheduler/SKILL.md; then exit 1; fi
if rg -n 'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]' \
  skills/aevatar-scheduler/SKILL.md; then exit 1; fi
build_skill_zip aevatar-scheduler 1.8
validate_live_zip "$ORNN_RELEASE_TMP/aevatar-scheduler-1.8.zip"
```

- [ ] **Step 7: Run the 1.8 GREEN scenario in a fresh context**

Use the exact RED prompt and only the candidate `SKILL.md`. Require all five
answers: canonical resource, distinct IDs, admission+reread, new-generation
reauthorization, pause/resume preservation, original-operation revocation
retry, and Agent Key-specific diagnosis. Record verbatim evidence. Tighten the
decision table or response recipe and rerun if a new mistake appears.

- [ ] **Step 8: Publish, download, and commit 1.8**

```bash
publish_skill_zip \
  8d4bb4e0-81e8-472b-bd71-2777130aba2f \
  1.8 \
  "$ORNN_RELEASE_TMP/aevatar-scheduler-1.8.zip"
verify_published_zip \
  8d4bb4e0-81e8-472b-bd71-2777130aba2f \
  aevatar-scheduler \
  1.8 \
  "$ORNN_RELEASE_TMP/aevatar-scheduler-1.8.zip"
git add skills/aevatar-scheduler/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "fix(skills): model canonical Aevatar Team automation"
```

---

### Task 3: Upgrade `aevatar-automation` to 1.2

**Files:**

- Modify: `skills/aevatar-automation/SKILL.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: `scheduled_agent_creator` and `agent_builder` implementation
  contracts.
- Produces: the independent scheduled-agent path referenced by the scheduler
  and skillset prompt.

- [ ] **Step 1: Run the 1.1 RED scenario**

```text
Read only skills/aevatar-automation/SKILL.md and act as the operating assistant.
Create an independent recurring Ornn skill agent that calls the exact connected
UserService 919208f4-d5f3-4840-8eba-8643820ce7f2 with slug snapshot
api-example, then explain the accepted receipt, credential lifetime/custody,
pause/resume, and delete. Also say what you would do instead if the request were
to schedule the already-bound Studio member m-alpha. Do not make real calls.
```

Expected RED: `required_service_slugs`, a shared 300-second fire token,
completion from accepted, loss of `scheduled_agent_creator`, or failure to route
the member request to the scheduler. Record exact evidence.

- [ ] **Step 2: Update frontmatter and creation contract**

Set version `"1.2"`. Start the description with `Use when` and preserve
scheduled-agent, one-shot, long-running automation, Lark negotiation,
`agent_builder`, and credential-diagnosis triggers.

Replace every creation reference to `required_service_slugs` with:

```text
required_nyx_services[] = {
  user_service_id,
  service_slug_snapshot
}
```

State that the exact UserService ID is authority and the slug snapshot is only
an integrity/display value. Preserve `nyx_user_service_id` for the exact
outbound provider where applicable.

- [ ] **Step 3: Replace the scheduled credential section**

Use these sections:

```markdown
### Choose independent agent versus Team automation
### scheduled_agent_creator
### Dedicated scheduled Agent Key
### Accepted creation and agent_builder verification
### One-shot reminders
### Long-running automation playbook
### agent_builder lifecycle
### Credential-source-first 401 diagnosis
```

State that scheduled-agent creation derives trusted ownership, revalidates an
exact plan, creates a constrained Agent Key, writes raw material only to the
Vault, persists a typed reference, and returns admission. Describe the default
projected key lifetime as 90 days subject to typed policy/configuration, never
as eternal. Pause/resume preserves it; delete revokes it and tombstones the
agent. A Team member request routes to `aevatar-scheduler`.

- [ ] **Step 4: Preserve the useful automation workflow**

Keep reuse-before-authoring, Ornn search/publish, interactive Lark negotiation,
delivery target binding, one-shot durability, and `agent_builder` commands.
Update the publish-then-schedule step to pass exact `required_nyx_services`
objects. Do not add Team automation APIs to this skill beyond the routing
boundary.

- [ ] **Step 5: Validate, GREEN-test, publish, and commit**

```bash
cd "$ORNN_WORKTREE"
validate_frontmatter skills/aevatar-automation
if rg -n 'required_service_slugs|one token.*whole run|fixed 300|fixed 5-minute' \
  skills/aevatar-automation/SKILL.md; then exit 1; fi
rg -n 'required_nyx_services|user_service_id|service_slug_snapshot|90 days|aevatar-scheduler' \
  skills/aevatar-automation/SKILL.md
if rg -n 'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]' \
  skills/aevatar-automation/SKILL.md; then exit 1; fi
build_skill_zip aevatar-automation 1.2
validate_live_zip "$ORNN_RELEASE_TMP/aevatar-automation-1.2.zip"
```

Run the exact RED prompt against the candidate in a fresh context. Require
`scheduled_agent_creator`, exact typed service refs, accepted-as-pending,
Agent Key/Vault custody, projected expiry, `agent_builder` verification and
management, and Team handoff. Refactor and rerun on any miss.

```bash
publish_skill_zip \
  1f8e2f07-67d3-4ac8-b7de-4596f36f4634 \
  1.2 \
  "$ORNN_RELEASE_TMP/aevatar-automation-1.2.zip"
verify_published_zip \
  1f8e2f07-67d3-4ac8-b7de-4596f36f4634 \
  aevatar-automation \
  1.2 \
  "$ORNN_RELEASE_TMP/aevatar-automation-1.2.zip"
git add skills/aevatar-automation/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "fix(skills): describe scheduled agent key custody"
```

---

### Task 4: Upgrade `aevatar-channels-delivery` to 1.2

**Files:**

- Modify: `skills/aevatar-channels-delivery/SKILL.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: typed credential-source evidence from scheduler/automation.
- Produces: source-first capability-tool and provider failure diagnosis.

- [ ] **Step 1: Run the 1.1 RED scenario**

```text
Read only skills/aevatar-channels-delivery/SKILL.md and diagnose this incident.
A scheduled workflow uses credentialSourceKind=scheduled_invocation_agent_key,
credentialGeneration=3, authorizationStatus=active, and an expiry tomorrow.
At fire+6 minutes one nyxid_proxy provider returns token_expired while a sibling
provider call still succeeds. State the likely causes, evidence to gather, and
safe next action. Do not make real calls.
```

Expected RED: automatic five-minute caller-token diagnosis or redesigning the
whole schedule without separating provider-specific failure. Record the exact
excerpt.

- [ ] **Step 2: Replace only the credential-diagnosis block**

Preserve `code_execute`, `nyxid_proxy`, PAT fallback, channel bots, route
selection, channel registrations, typed Lark tools, relay reply semantics, and
delivery targets.

Use this decision table:

| Source | First evidence | Correct diagnosis boundary |
| --- | --- | --- |
| `scheduled_invocation_agent_key` | key expiry, generation, Vault resolution, authorization fact, exact grants, revocation | dedicated key/reference path; no assumed 300-second broker TTL |
| binding exchange | actual exchanged token `iat`/`exp` and provider contract | short-lived bearer only after token class is proved |
| interactive session | its own caller token class | session-specific lifetime |
| one provider fails while siblings work | provider response and provider credential path | isolate provider-specific failure |

Do not ask to print or decode raw token material. Numeric `iat`/`exp` facts may
be reported only from a safe typed inspection surface.

- [ ] **Step 3: Validate, GREEN-test, publish, and commit**

```bash
cd "$ORNN_WORKTREE"
validate_frontmatter skills/aevatar-channels-delivery
if rg -n 'scheduled run.*fixed 300|steps starting ~5 minutes.*expected|one token.*whole run' \
  skills/aevatar-channels-delivery/SKILL.md; then exit 1; fi
rg -n 'scheduled_invocation_agent_key|credentialGeneration|binding|interactive|sibling' \
  skills/aevatar-channels-delivery/SKILL.md
if rg -n 'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]' \
  skills/aevatar-channels-delivery/SKILL.md; then exit 1; fi
build_skill_zip aevatar-channels-delivery 1.2
validate_live_zip "$ORNN_RELEASE_TMP/aevatar-channels-delivery-1.2.zip"
```

Run the exact RED prompt against the candidate. Require Agent Key-specific
checks and provider isolation. Refactor and rerun on any miss.

```bash
publish_skill_zip \
  d2b575d3-0d80-4167-99e4-6161be47db7f \
  1.2 \
  "$ORNN_RELEASE_TMP/aevatar-channels-delivery-1.2.zip"
verify_published_zip \
  d2b575d3-0d80-4167-99e4-6161be47db7f \
  aevatar-channels-delivery \
  1.2 \
  "$ORNN_RELEASE_TMP/aevatar-channels-delivery-1.2.zip"
git add skills/aevatar-channels-delivery/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "fix(skills): diagnose scheduled credentials by source"
```

---

### Task 5: Upgrade `aevatar-triage` to 1.4

**Files:**

- Modify: `skills/aevatar-triage/SKILL.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: Aevatar/NyxID/Ornn evidence boundaries and typed schedule states.
- Produces: code-grounded investigation that does not universalize one token
  class or prime projections from queries.

- [ ] **Step 1: Run the 1.3 RED scenario**

```text
Read only skills/aevatar-triage/SKILL.md and triage two observations without
making calls. Observation A: canonical Team automation create returned 202
accepted but the owner-scoped read model still has the old stateVersion.
Observation B: a scheduled run reports token_expired at fire+6 minutes and the
automation says credentialSourceKind=scheduled_invocation_agent_key,
credentialGeneration=2, revocationPending=false. Separate facts, hypotheses,
next probes, and evidence stages. Do not recommend creating a replacement
schedule or forcing a projection refresh.
```

Expected RED: universal broker-TTL attribution, accepted-as-complete, or a
normal query-path projection repair. Record exact evidence.

- [ ] **Step 2: Add schedule resource and evidence-stage classification**

Preserve the three-layer attribution model, deployed-commit pinning,
code-citation bar, contract verdict, issue confirmation gate, negative control,
and provider/channel diagnostics.

Add an early schedule classification table for Team automation, independent
scheduled agent, and generic schedule. Add these separate evidence stages:

```text
admission -> committed schedule/credential state -> read-model visibility ->
fire/run start -> run completion -> external side effect
```

No stage may be inferred from the previous stage.

- [ ] **Step 3: Replace universal TTL guidance with typed evidence**

For Agent Key-backed automation, collect canonical owner tuple,
`credentialSourceKind`, `authorizationStatus`, `credentialExpiresAtUtc`,
`credentialGeneration`, `stateVersion`, `lastAuthorizationErrorCode`,
`revocationPending`, `nyxIdRevocationStatus`, `vaultRevocationStatus`, and the
exact failure timestamp. Inspect Vault resolution/reference integrity,
committed authority, exact grants, expiry, generation, and revocation without
printing secret material.

For binding exchange, inspect the actual token class and verified lifetime.
Keep interactive credentials separate. Add stable recoveries for
`authorization_plan_changed`, `needs_authorization`, `revocation_pending`, and
projection pending. State that query-time replay/priming is forbidden.

- [ ] **Step 4: Validate, GREEN-test, publish, and commit**

```bash
cd "$ORNN_WORKTREE"
validate_frontmatter skills/aevatar-triage
if rg -n 'by design.*BROKER_ACCESS_TTL_SECS|scheduled run.*~5 min.*expected' \
  skills/aevatar-triage/SKILL.md; then exit 1; fi
rg -n 'credentialSourceKind|credentialGeneration|stateVersion|nyxIdRevocationStatus|vaultRevocationStatus|query-time' \
  skills/aevatar-triage/SKILL.md
if rg -n 'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]' \
  skills/aevatar-triage/SKILL.md; then exit 1; fi
build_skill_zip aevatar-triage 1.4
validate_live_zip "$ORNN_RELEASE_TMP/aevatar-triage-1.4.zip"
```

Run the RED prompt against the candidate. Require honest stage separation,
source-first diagnosis, no replacement schedule, and no projection priming.
Refactor and rerun on any miss.

```bash
publish_skill_zip \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  1.4 \
  "$ORNN_RELEASE_TMP/aevatar-triage-1.4.zip"
verify_published_zip \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  aevatar-triage \
  1.4 \
  "$ORNN_RELEASE_TMP/aevatar-triage-1.4.zip"
git add skills/aevatar-triage/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "fix(skills): triage typed schedule credential evidence"
```

---

### Task 6: Upgrade `aevatar-platform-map` to 1.8

**Files:**

- Modify: `skills/aevatar-platform-map/SKILL.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: all corrected resource boundaries.
- Produces: the family entry point and scheduler handoff model.

- [ ] **Step 1: Run the 1.7 RED scenario**

```text
Read only skills/aevatar-platform-map/SKILL.md and route these requests without
making calls: (1) schedule the already-bound member m-alpha whose draft is
wf-alpha and published service is svc-alpha, (2) create a reusable independent
scheduled Ornn skill agent, and (3) schedule a raw service envelope. Show the
resource ownership model and keep every identity distinct.
```

Expected RED: a false linear `member -> service -> schedule` lifecycle,
generic `/api/schedules` as the member default, loss of member ownership after
publish, or no independent-agent sibling. Record exact evidence.

- [ ] **Step 2: Replace the object model and router**

Set version `"1.8"` and start the description with `Use when`. Use this product
ownership model exactly:

```text
scope
  -> team
     -> member
        -> implementation surface (workflow | script | gagent)
        -> publishedServiceId
        -> automations

scope owner
  -> independent scheduled skill agents
  -> generic platform schedules
```

Publishing adds a callable service identity; it does not transform or erase
the member. `workflow` in the canonical member editor route is an
implementation surface, not a workflow resource identity.

Route all scheduling intent to `aevatar-scheduler`; that skill chooses the
three resources. Route scheduled-agent authoring/one-shot details onward to
`aevatar-automation`. Keep feasibility, workflow authoring, Team building,
service publishing, invocation, channels, and triage spokes intact.

- [ ] **Step 3: Update the golden path and identity guard**

The golden path must end with two sibling options:

```text
publish member service -> optionally automate the same member through its
scope/team/member owner path

create an independent scheduled agent -> scheduled_agent_creator +
agent_builder
```

Generic service/envelope scheduling remains an advanced platform resource.
Use `m-alpha`, `wf-alpha`, and `svc-alpha` to demonstrate non-equality. Remove
the global five-minute claim.

- [ ] **Step 4: Validate, GREEN-test, publish, and commit**

```bash
cd "$ORNN_WORKTREE"
validate_frontmatter skills/aevatar-platform-map
rg -n 'publishedServiceId|automations|independent scheduled|generic platform|aevatar-scheduler|m-alpha|wf-alpha|svc-alpha' \
  skills/aevatar-platform-map/SKILL.md
if rg -n 'member.*-> service.*-> schedule|fire-time broker token.*300|fixed 5-minute' \
  skills/aevatar-platform-map/SKILL.md; then exit 1; fi
if rg -n 'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]' \
  skills/aevatar-platform-map/SKILL.md; then exit 1; fi
build_skill_zip aevatar-platform-map 1.8
validate_live_zip "$ORNN_RELEASE_TMP/aevatar-platform-map-1.8.zip"
```

Run the exact RED prompt against the candidate. Require all three resources,
correct ownership, correct skill handoff, and distinct identities. Refactor and
rerun on any miss.

```bash
publish_skill_zip \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  1.8 \
  "$ORNN_RELEASE_TMP/aevatar-platform-map-1.8.zip"
verify_published_zip \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  aevatar-platform-map \
  1.8 \
  "$ORNN_RELEASE_TMP/aevatar-platform-map-1.8.zip"
git add skills/aevatar-platform-map/SKILL.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md
git commit -m "fix(skills): map Aevatar resource ownership"
```

---

### Task 7: Publish `aevatar-platform` Skillset Revision 1.12

**Files:**

- Create: `skillsets/aevatar-platform/manifest.json`
- Create: `skillsets/aevatar-platform/master-prompt.md`
- Create: `.changeset/calm-aevatar-agent-keys.md`
- Modify:
  `docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md`

**Interfaces:**

- Consumes: five verified target skill versions and seven unchanged pins.
- Produces: immutable skillset revision `1.12` with a twelve-member all-public
  closure.

- [ ] **Step 1: Run the 1.11 master-prompt RED scenario**

Use the exact `instructions` returned by
`GET /api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad?version=1.11`
in a fresh context:

```text
Using only the supplied aevatar-platform master prompt, route three requests:
schedule existing member m-alpha, create an independent scheduled Ornn skill
agent, and diagnose token_expired at fire+6 minutes for
credentialSourceKind=scheduled_invocation_agent_key. Keep wf-alpha and
svc-alpha distinct from m-alpha.
```

Expected RED: the master prompt routes late errors to a universal 300-second
model or presents scheduling as one lifecycle stage. Record the exact excerpt.

- [ ] **Step 2: Write the exact twelve-member manifest**

```json
{
  "description": "Entry point and router for Aevatar workflows, Teams, member services, member automations, independent scheduled agents, generic schedules, channels, and diagnostics.",
  "kind": "generic",
  "tags": [],
  "members": [
    "fallback-to-calling-agent@1.0",
    "aevatar-workflow-authoring@1.5",
    "aevatar-team-builder@1.3",
    "aevatar-scheduler@1.8",
    "aevatar-service-publisher@1.5",
    "aevatar-platform-map@1.8",
    "aevatar-feasibility-advisor@1.1",
    "aevatar-triage@1.4",
    "firecrawl-via-nyxid@1.1",
    "github-via-nyxid@1.0",
    "aevatar-automation@1.2",
    "aevatar-channels-delivery@1.2"
  ]
}
```

Do not include a `version`; Ornn system-assigns the next minor revision.

- [ ] **Step 3: Write the master prompt as a positive router contract**

Use these sections in this order:

```markdown
# Aevatar platform router
## Product ownership model
## Choose caller surface
## Choose the scheduled resource
## Identity boundaries
## Scheduled credential truth
## Route to one owning skill
## Honesty and safety
```

The scheduled-resource section must contain the same three-row decision table
as the scheduler. The credential section must say dedicated Team/scheduled
agent keys are constrained, Vault-backed durable references late-resolved per
credential use, while only a verified binding-exchange source may imply a
short-lived bearer. Keep the prompt below 8000 characters.

- [ ] **Step 4: Validate the manifest and master prompt locally**

```bash
cd "$ORNN_WORKTREE"
jq -e '
  has("version") | not
  and .kind == "generic"
  and (.members | length) == 12
  and (.members | unique | length) == 12
  and (.members | index("aevatar-scheduler@1.8")) != null
  and (.members | index("aevatar-automation@1.2")) != null
  and (.members | index("aevatar-channels-delivery@1.2")) != null
  and (.members | index("aevatar-triage@1.4")) != null
  and (.members | index("aevatar-platform-map@1.8")) != null
' skillsets/aevatar-platform/manifest.json
test "$(wc -c < skillsets/aevatar-platform/master-prompt.md | tr -d ' ')" -le 8000
if rg -n 'fire-time broker token.*300|every scheduled run|one token.*whole run|required_service_slugs' \
  skillsets/aevatar-platform/master-prompt.md; then exit 1; fi
jq --rawfile instructions skillsets/aevatar-platform/master-prompt.md \
  '. + {instructions: $instructions}' \
  skillsets/aevatar-platform/manifest.json \
  > "$ORNN_RELEASE_TMP/aevatar-platform-1.12-publish.json"
jq -e '.instructions | length > 0 and length <= 8000' \
  "$ORNN_RELEASE_TMP/aevatar-platform-1.12-publish.json"
```

- [ ] **Step 5: GREEN-test the candidate master prompt**

Run the exact 1.11 RED scenario with only the candidate master prompt. Require
the canonical Team owner path/scheduler, independent agent/automation,
generic schedule/scheduler, distinct IDs, and Agent Key-specific diagnosis.
Refactor and rerun on any miss.

- [ ] **Step 6: Recheck every member immediately before publication**

GET all twelve exact refs or resolve them through a dry read. Assert the five
new versions and seven unchanged versions are readable and public. GET current
skillset detail and assert it is still `1.11`; if it advanced unexpectedly,
stop and reconcile before publishing.

- [ ] **Step 7: Publish the stable skillset GUID**

```bash
nyxid proxy request ornn-api \
  '/api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad' \
  --via-service "$ORNN_USER_SERVICE_ID" \
  --method PUT \
  --header 'Content-Type:application/json' \
  --data "@$ORNN_RELEASE_TMP/aevatar-platform-1.12-publish.json" \
  --output json \
  | jq -e '
      .error == null
      and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
      and .data.version == "1.12"
      and .data.latestVersion == "1.12"
      and .data.memberVisibilityState == "all-public"
      and (.data.members | length) == 12
    '
```

If the response is ambiguous, query version history and exact `1.12` detail
before retrying. Never publish a second logical revision to mask uncertainty.

- [ ] **Step 8: Verify detail, closure, visibility, and rollback**

Read exact `1.12` detail, exact `1.12` closure, version history, and exact
`1.11` detail. Assert:

```text
stable GUID unchanged
latestVersion == 1.12
memberVisibilityState == all-public
publicMemberCount == 12
unreadableMembers is empty
closure contains exactly the twelve intended direct members
five changed pins equal their target versions
seven unchanged pins equal their old versions
no dependency conflict or unexpected transitive member
1.11 remains readable with its old prompt and pins
```

- [ ] **Step 9: Add the content changeset and commit**

Create:

```markdown
---
---

Align Aevatar scheduling skills with dedicated Agent Key credential semantics.
```

Then commit:

```bash
git add \
  skillsets/aevatar-platform/manifest.json \
  skillsets/aevatar-platform/master-prompt.md \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md \
  .changeset/calm-aevatar-agent-keys.md
git commit -m "fix(skillsets): align Aevatar scheduled Agent Key semantics"
```

---

### Task 8: Run Final Release Verification

**Files:**

- Verify all changed files; no new implementation files.

**Interfaces:**

- Consumes: committed Ornn source plus verified immutable remote versions.
- Produces: evidence-backed handoff without a Git push or PR.

- [ ] **Step 1: Verify the local branch scope and cleanliness**

```bash
cd "$ORNN_WORKTREE"
git diff origin/develop...HEAD --check
git status --short
git diff --name-status origin/develop...HEAD
```

Expected changed paths are limited to five `skills/aevatar-*/SKILL.md` files,
the validation record, two skillset source files, and one changeset. Status is
clean.

- [ ] **Step 2: Rebuild and revalidate all five exact source packages**

For every skill, rerun `validate_frontmatter`, `build_skill_zip`, and
`validate_live_zip`. The rebuilt ZIP may have different timestamp bytes from
the published ZIP; semantic verification must compare `SKILL.md` bytes and
file lists, while the per-skill publication record retains the original exact
candidate/download SHA pair.

- [ ] **Step 3: Run the complete stale-semantic and secret scans**

```bash
cd "$ORNN_WORKTREE"
if rg -n \
  'required_service_slugs|every scheduled run uses|every scheduled fire exchanges|one token.*whole run|202 Accepted proves|pause revokes|memberId == workflowId' \
  skills/aevatar-{scheduler,automation,channels-delivery,triage,platform-map} \
  skillsets/aevatar-platform; then exit 1; fi
if rg -n \
  'Authorization:|eyJ[A-Za-z0-9_-]+\.|vault://|sk-[A-Za-z0-9]|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY' \
  skills/aevatar-{scheduler,automation,channels-delivery,triage,platform-map} \
  skillsets/aevatar-platform \
  docs/validation/aevatar-platform-scheduled-agent-key-evaluation.md; then exit 1; fi
```

Manually inspect every remaining `300`, `5 minute`, `binding`, `digest`, and
`Metadata` occurrence. A numeric short TTL is allowed only in an explicitly
scoped binding-exchange or other identified token-class discussion. No actual
digest value is allowed.

- [ ] **Step 4: Reread every remote stable identity and immutable version**

For each skill, assert stable GUID, target latest version, public visibility,
the downloaded target package hash recorded in the evaluation document, and
continued readability of the old version. For the skillset, repeat the Task 7
detail/closure/rollback assertions.

- [ ] **Step 5: Confirm the operational boundary**

Review the executed command history and evaluation record. The only write
paths must be:

```text
PUT /api/v1/skills/{one-of-five-stable-guids}
PUT /api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad
```

There must be no Aevatar, NyxID key, Vault, schedule, workflow, run, Team,
member, or canary mutation.

- [ ] **Step 6: Report the result**

Report the five stable GUID/version/hash triples, skillset GUID/revision,
twelve exact pins, validation commands/results, local branch/worktree, local
commit range, and the fact that no Git push/PR or production scheduled resource
mutation occurred. If any gate failed, report that exact partial state and the
last verified immutable rollback point instead of claiming completion.
