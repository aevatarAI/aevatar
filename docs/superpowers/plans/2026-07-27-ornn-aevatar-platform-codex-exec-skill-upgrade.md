# Ornn Aevatar Platform Codex Exec Skill Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish corrected canonical Ornn skills for Aevatar `codex_exec`, route them from `aevatar-platform`, and verify the immutable published packages and final skillset closure.

**Architecture:** Treat the live Ornn registry as the publication authority and exact Aevatar/chrono-sandbox source revisions as the behavior authority. Download each immutable baseline ZIP into an isolated temporary workspace, demonstrate the old behavior failing, patch one skill, validate and forward-test it, publish that one immutable version, and read it back before touching the next skill. Publish the skillset only after all exact member refs have passed independent readback and dependency-conflict preflight.

**Tech Stack:** Ornn `/api/v1` skill and skillset APIs, NyxID CLI proxy transport, ZIP packages, Markdown/YAML skill files, `jq`, `rg`, `shasum`, OpenAI Codex CLI fresh-context evals, Git documentation.

## Global Constraints

- Use the exact design in `docs/superpowers/specs/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade-design.md`.
- Do not modify Aevatar, NyxID, Ornn, or chrono-sandbox production source as part of this implementation.
- Preserve all unrelated dirty-worktree changes.
- Build candidate packages outside every source repository under `${TMPDIR:-/tmp}/aevatar-platform-codex-skill-upgrade-2026-07-27`.
- Use `apply_patch` for candidate text edits; use `unzip` and `zip` only for mechanical package extraction/repacking.
- Never print, copy, persist in the workspace, or pass between profiles a NyxID bearer, refresh token, API key, Codex credential, or delegation token.
- The Codex-skill owner must be exactly `2db990b5-29ea-4a32-acf5-0008420afa1f`; use the isolated NyxID profile `codex-skill-owner`.
- The platform-skill owner must be exactly `5d0d7b72-acff-49af-bb1b-9f30bbb7c102`; use the existing default NyxID profile.
- Publish skills only by GUID and exact increasing version; publish the skillset only by GUID and omit `version` because Ornn assigns the next minor revision.
- Never use `skip_validation=true`.
- A NyxID proxy CLI exit code is not proof of an upstream 2xx; parse every JSON response and assert `error == null` plus the expected identity/version.
- Verify every published skill both as JSON files and as downloaded raw ZIP bytes whose SHA-256 equals the published `skillHash` and the local candidate hash.
- Managed `codex_exec` accepts only `managed_sandbox + empty_git`, a prompt of at most 6000 UTF-8 bytes, and a timeout at most 180 seconds.
- Private SSH accepts only nested `target.private_ssh`, no workspace, a prompt of at most 6000 UTF-8 bytes, and a timeout at most 300 seconds.
- Managed isolation is the outer gVisor workload. Do not teach Landlock, Bubblewrap, sandbox-side Credential Vault substitution, or a TLS credential proxy as the current design.
- Keep `memberId`, `workflowId`, and `publishedServiceId` semantically separate. Do not restore a global Build/Bind/Invoke/Observe or `scope -> team -> member -> service` lifecycle.
- Complete RED, GREEN, validation, forward test, publication, and exact readback for one skill before moving to the next.
- Do not spawn subagents unless the user later explicitly requests delegation; use isolated `codex exec --ephemeral` processes for fresh-context skill evals.
- Immediately before publishing the first candidate, re-read `NyxIdCodexExecTool`, `ManagedCodexExecutionCoordinator`, `NyxIdManagedCodexChronoTransport`, `PrivateSshCodexExecutionAdapter`, and the chrono-sandbox managed command/profile. If any public target, field, limit, route, result, or failure semantic differs from the approved design, stop and revise the specification instead of publishing stale guidance. Transport-only timeout grace does not change the caller's 180/300-second business limits.

## File Map

Repository files:

- `docs/superpowers/plans/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade.md`: this executable plan.
- `docs/superpowers/specs/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade-design.md`: update only the status and final published evidence after the registry work passes.

Temporary package files:

- `$WORK_ROOT/baselines/<skill>/<version>/<skill>/...`: byte-verified immutable old package.
- `$WORK_ROOT/candidates/<skill>/<skill>/...`: candidate package edited from the old package.
- `$WORK_ROOT/packages/<skill>-<version>.zip`: exact ZIP uploaded to Ornn.
- `$WORK_ROOT/readback/<skill>-<version>.zip`: exact ZIP downloaded after publication.
- `$WORK_ROOT/evals/<skill>/<case>/<variant>.md`: no-skill, old-skill, and candidate fresh-context responses.
- `$WORK_ROOT/bin/release-skill.sh`: deterministic validate/publish/readback helper for one immutable skill version.
- `$WORK_ROOT/skillset-publish.json`: complete proposed skillset request with no owner-supplied version.

---

### Task 1: Establish Reproducible Packages, Eval Isolation, and Both Owner Identities

**Files:**
- Create: `$WORK_ROOT/baselines/**`
- Create: `$WORK_ROOT/candidates/**`
- Create: `$WORK_ROOT/packages/**`
- Create: `$WORK_ROOT/readback/**`
- Create: `$WORK_ROOT/evals/**`
- Create: `$WORK_ROOT/bin/release-skill.sh`

**Interfaces:**
- Consumes: current Ornn skill versions and the two NyxID identities from the approved design.
- Produces: byte-verified baseline packages, isolated candidate folders, and authenticated owner profiles used by Tasks 2–7.

- [ ] **Step 1: Define the isolated workspace and assert repository cleanliness boundaries**

Run:

```bash
export WORK_ROOT="${TMPDIR:-/tmp}/aevatar-platform-codex-skill-upgrade-2026-07-27"
mkdir -p "$WORK_ROOT/baselines" "$WORK_ROOT/candidates" "$WORK_ROOT/packages" "$WORK_ROOT/readback" "$WORK_ROOT/evals"
git status --short
git -C ../Ornn status --short
git -C ../chrono-sandbox status --short
git -C ../.worktrees/chrono-sandbox-managed-codex status --short
```

Expected: the known Aevatar user changes remain visible; no command mutates any repository; `WORK_ROOT` is outside all repositories.

- [ ] **Step 2: Assert the platform owner and Ornn permissions**

Run:

```bash
platform_identity="$(nyxid whoami --output json)"
jq -e '.id == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"' <<<"$platform_identity"
platform_ornn_identity="$(nyxid proxy request ornn-api '/api/v1/me' --method GET --output json)"
jq -e '.error == null and (.data.permissions | index("ornn:skill:update")) != null and (.data.permissions | index("ornn:skill:read")) != null' <<<"$platform_ornn_identity"
```

Expected: both `jq -e` commands exit 0 without printing any credential.

- [ ] **Step 3: Authenticate the canonical Codex-skill owner in an isolated profile**

Run:

```bash
nyxid login --profile codex-skill-owner --base-url https://nyx-api.chrono-ai.fun
```

Expected: the browser-based NyxID flow completes. Do not continue until it reports success.

Then run:

```bash
codex_owner_identity="$(nyxid whoami --profile codex-skill-owner --output json)"
jq -e '.id == "2db990b5-29ea-4a32-acf5-0008420afa1f"' <<<"$codex_owner_identity"
codex_owner_ornn_identity="$(nyxid proxy request ornn-api '/api/v1/me' --profile codex-skill-owner --method GET --output json)"
jq -e '.error == null and (.data.permissions | index("ornn:skill:update")) != null and (.data.permissions | index("ornn:skill:read")) != null' <<<"$codex_owner_ornn_identity"
```

Expected: both assertions pass. If the authenticated user differs, stop and log out only the `codex-skill-owner` profile; never publish under the wrong identity.

- [ ] **Step 4: Download and hash-check the five immutable baselines**

Use this exact baseline matrix:

```text
aevatar-codex-exec-workflow-sample  2.0  f69ba2d0-4ae9-4ae5-8fd6-92b287695427  codex-skill-owner
aevatar-codex-exec-node-setup       3.0  9d4361eb-602e-4186-a12a-6b95801906c4  codex-skill-owner
aevatar-platform-map                1.7  b8bf9e98-2658-4e09-9c51-2e4958137091  default
aevatar-feasibility-advisor         1.1  d0619556-402e-4baf-aa26-fbfe78ac937c  default
aevatar-triage                      1.3  fbd40315-317f-4f80-9885-b44b83e1a204  default
```

Download with the profile declared by the matrix and assert every GUID/version
before copying a candidate:

```bash
while IFS=$'\t' read -r name version guid profile; do
  profile_args=()
  if [[ "$profile" == "codex-skill-owner" ]]; then
    profile_args=(--profile codex-skill-owner)
  fi

  detail="$(nyxid proxy request ornn-api "/api/v1/skills/$name" \
    "${profile_args[@]}" --method GET --output json)"
  jq -e --arg name "$name" --arg guid "$guid" \
    '.error == null and .data.name == $name and .data.guid == $guid' \
    <<<"$detail"

  mkdir -p "$WORK_ROOT/baselines/$name/$version"
  nyxid proxy request ornn-api "/api/v1/skills/$name/versions/$version/download" \
    "${profile_args[@]}" --method GET --stream \
    > "$WORK_ROOT/baselines/$name/$version.zip"
  unzip -t "$WORK_ROOT/baselines/$name/$version.zip"
  versions="$(nyxid proxy request ornn-api "/api/v1/skills/$name/versions" \
    "${profile_args[@]}" --method GET --output json)"
  expected_hash="$(jq -er --arg version "$version" \
    '.data.items[] | select(.version == $version) | .skillHash' <<<"$versions")"
  actual_hash="$(shasum -a 256 "$WORK_ROOT/baselines/$name/$version.zip" | awk '{print $1}')"
  test "$actual_hash" = "$expected_hash"
  unzip -q "$WORK_ROOT/baselines/$name/$version.zip" -d "$WORK_ROOT/baselines/$name/$version"
  test -f "$WORK_ROOT/baselines/$name/$version/$name/SKILL.md"
  rm -rf "$WORK_ROOT/candidates/$name"
  mkdir -p "$WORK_ROOT/candidates/$name"
  cp -R "$WORK_ROOT/baselines/$name/$version/$name" "$WORK_ROOT/candidates/$name/$name"
done <<'BASELINES'
aevatar-codex-exec-workflow-sample	2.0	f69ba2d0-4ae9-4ae5-8fd6-92b287695427	codex-skill-owner
aevatar-codex-exec-node-setup	3.0	9d4361eb-602e-4186-a12a-6b95801906c4	codex-skill-owner
aevatar-platform-map	1.7	b8bf9e98-2658-4e09-9c51-2e4958137091	default
aevatar-feasibility-advisor	1.1	d0619556-402e-4baf-aa26-fbfe78ac937c	default
aevatar-triage	1.3	fbd40315-317f-4f80-9885-b44b83e1a204	default
BASELINES
```

Expected: all ZIP integrity tests pass, every root folder matches its skill name, and candidates are byte-derived from exact immutable baselines.

- [ ] **Step 5: Create and syntax-check the deterministic release helper**

Use `apply_patch` to create `$WORK_ROOT/bin/release-skill.sh` with this complete content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 8 ]]; then
  echo "usage: $0 <validate|publish> <default|profile> <expected-owner-id> <name> <version> <guid> <package.zip> <candidate-skill-dir>" >&2
  exit 64
fi

mode="$1"
profile="$2"
expected_owner="$3"
name="$4"
version="$5"
guid="$6"
package="$7"
candidate_dir="$8"

if [[ "$mode" != "validate" && "$mode" != "publish" ]]; then
  echo "mode must be validate or publish" >&2
  exit 64
fi

profile_args=()
if [[ "$profile" != "default" ]]; then
  profile_args=(--profile "$profile")
fi

test -f "$package"
test -f "$candidate_dir/SKILL.md"
unzip -t "$package" >/dev/null

detail="$(nyxid proxy request ornn-api "/api/v1/skills/$name" \
  "${profile_args[@]}" --method GET --output json)"
jq -e \
  --arg name "$name" \
  --arg guid "$guid" \
  --arg owner "$expected_owner" \
  '.error == null and .data.name == $name and .data.guid == $guid and .data.createdBy == $owner' \
  <<<"$detail" >/dev/null

validation="$(nyxid proxy request ornn-api '/api/v1/skill-format/validate' \
  "${profile_args[@]}" --method POST --data "@$package" \
  --header 'Content-Type:application/zip' --output json)"
jq -e '.error == null and .data.valid == true and (.data.violations | length) == 0' \
  <<<"$validation" >/dev/null

candidate_hash="$(shasum -a 256 "$package" | awk '{print $1}')"
versions="$(nyxid proxy request ornn-api "/api/v1/skills/$name/versions" \
  "${profile_args[@]}" --method GET --output json)"
jq -e '.error == null' <<<"$versions" >/dev/null
existing_hash="$(jq -r --arg version "$version" \
  '[.data.items[] | select(.version == $version) | .skillHash][0] // ""' \
  <<<"$versions")"

if [[ -n "$existing_hash" && "$existing_hash" != "$candidate_hash" ]]; then
  echo "$name@$version already exists with a different hash" >&2
  exit 65
fi

if [[ "$mode" == "validate" ]]; then
  echo "validated $name@$version sha256=$candidate_hash"
  exit 0
fi

if [[ -n "$existing_hash" ]]; then
  : # Safe resume: exact immutable version and ZIP hash already exist.
else
  publish="$(nyxid proxy request ornn-api "/api/v1/skills/$guid" \
    "${profile_args[@]}" --method PUT --data "@$package" \
    --header 'Content-Type:application/zip' --output json)"
  jq -e --arg name "$name" --arg version "$version" \
    '.error == null and .data.name == $name and .data.version == $version' \
    <<<"$publish" >/dev/null
fi

versions="$(nyxid proxy request ornn-api "/api/v1/skills/$name/versions" \
  "${profile_args[@]}" --method GET --output json)"
jq -e --arg version "$version" --arg hash "$candidate_hash" \
  '.error == null and any(.data.items[]; .version == $version and .skillHash == $hash)' \
  <<<"$versions" >/dev/null

readback_dir="${WORK_ROOT:?WORK_ROOT is required}/readback"
mkdir -p "$readback_dir"
readback_zip="$readback_dir/$name-$version.zip"
nyxid proxy request ornn-api "/api/v1/skills/$name/versions/$version/download" \
  "${profile_args[@]}" --method GET --stream > "$readback_zip"
unzip -t "$readback_zip" >/dev/null
cmp "$package" "$readback_zip"

json_file="$readback_dir/$name-$version.json"
nyxid proxy request ornn-api "/api/v1/skills/$name/json?version=$version" \
  "${profile_args[@]}" --method GET --output json > "$json_file"
jq -e --arg name "$name" --arg version "$version" \
  '.error == null and .data.name == $name and .data.version == $version' \
  "$json_file" >/dev/null

local_count="$(find "$candidate_dir" -type f | wc -l | tr -d ' ')"
remote_count="$(jq -r '.data.files | length' "$json_file")"
test "$local_count" = "$remote_count"

while IFS= read -r -d '' local_file; do
  relative="${local_file#"$candidate_dir"/}"
  jq -e --arg path "$relative" '.data.files | has($path)' "$json_file" >/dev/null
  remote_file="$(mktemp "${TMPDIR:-/tmp}/ornn-skill-file.XXXXXX")"
  jq -jr --arg path "$relative" '.data.files[$path]' "$json_file" > "$remote_file"
  cmp "$local_file" "$remote_file"
  rm -f "$remote_file"
done < <(find "$candidate_dir" -type f -print0)

echo "published-and-verified $name@$version sha256=$candidate_hash"
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/release-skill.sh"
bash -n "$WORK_ROOT/bin/release-skill.sh"
```

Expected: syntax check exits 0. The helper prints no credential and performs no publish in `validate` mode.

- [ ] **Step 6: Record fresh-context eval commands without leaking this design**

Use a temporary working directory that contains only the relevant old or candidate skill. For every scenario in later tasks, run each variant with a new process:

```bash
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$eval_dir" \
  -o "$output_file" \
  "$prompt"
```

For the no-skill control, `eval_dir` is empty and the prompt does not mention a skill. For old/candidate variants, `eval_dir` contains only the corresponding package and the prompt begins: `Read <skill>/SKILL.md and any directly referenced file needed for this request, then follow it.` Never include the intended answer, the identified drift, or the design document in an eval prompt.

---

### Task 2: Upgrade and Publish `aevatar-codex-exec-workflow-sample@3.0`

**Files:**
- Modify: `$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample/aevatar-codex-exec-workflow-sample/SKILL.md`
- Modify: `$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample/aevatar-codex-exec-workflow-sample/assets/codex-exec-check.yaml`
- Preserve: `$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample/aevatar-codex-exec-workflow-sample/assets/codex-exec-private-ssh-check.yaml`
- Test: `$WORK_ROOT/evals/aevatar-codex-exec-workflow-sample/**`

**Interfaces:**
- Consumes: typed tool payloads and result semantics from the approved design.
- Produces: public immutable `aevatar-codex-exec-workflow-sample@3.0`, consumed by Task 3 and Task 7.

- [ ] **Step 1: Verify RED against the exact 2.0 package**

Run:

```bash
old="$WORK_ROOT/baselines/aevatar-codex-exec-workflow-sample/2.0/aevatar-codex-exec-workflow-sample"
test "$(rg -l 'operator-managed OpenSandbox|llm_proxy_scope_missing|Credential Vault|Landlock' "$old" | wc -l | tr -d ' ')" -gt 0
! rg -q 'chrono-sandbox|gVisor|managed_proxy_timeout|managed_credential_unavailable' "$old/SKILL.md"
```

Expected: the first assertion passes and the negative assertion exposes missing current terminology. Treat this as the reference-skill RED.

Run a no-skill and old-skill fresh-context scenario:

```text
Scenario: A managed codex_exec verification failed with managed_proxy_timeout. Explain which result fields prove success, which layer owns the timeout, and whether I should repair Landlock or Credential Vault.
```

Expected old-skill failure: it routes the user toward OpenSandbox-era Vault/Landlock diagnoses or lacks the current `managed_proxy_timeout` boundary.

- [ ] **Step 2: Write the minimal 3.0 contract**

Apply these exact semantic changes:

```yaml
version: "3.0"
description: Mount and run harmless Aevatar workflows that prove codex_exec works through either the operator-managed chrono-sandbox/gVisor target or a private NyxID node-backed SSH target. Use after managed eligibility and required NyxID UserServices are ready, or after configuring a personal SSH node; also use when diagnosing typed managed or private-route failures before real tasks.
```

The body must contain these sections in this order:

1. `# Verify Aevatar codex_exec`
2. `## Choose exactly one proof`
3. `## Guardrails`
4. `## Mount`
5. `## Managed proof`
6. `## Private SSH proof`
7. `## Diagnose by boundary`

Required content:

- Managed proof calls `codex-exec-check` with no caller-controlled routing and retains the exact typed payload already present in 2.0.
- Managed success requires `status=succeeded`, `target=managed_sandbox`, trimmed `output=CODEX_EXEC_READY`, `exit_code=0`, and non-empty sanitized `diagnostic_id`; `elapsed_ms` is optional.
- Private proof retains the existing nested `target.private_ssh` workflow and validates NyxID SSH response fields `exit_code=0`, `timed_out=false`, and trimmed stdout `CODEX_EXEC_READY`.
- State explicitly that private SSH output is not converted into the managed JSON shape.
- Diagnose `target_not_configured`/`managed_target_disabled`, `managed_feature_not_enabled`, `managed_user_services_unavailable`, `managed_credential_unavailable`, `managed_proxy_authorization_denied`, `managed_proxy_target_unavailable`, `managed_proxy_timeout`, `managed_proxy_unavailable`, `managed_response_invalid`, `managed_response_too_large`, `managed_execution_nonzero_exit`, and private node/service/principal/Codex failures at their actual layer.
- Identify gVisor as the managed isolation boundary. State once that Landlock, Bubblewrap, sandbox-side Credential Vault substitution, and a TLS credential proxy are not this runtime's repair path.
- Never tell a normal caller to provision a key, pass a raw token, choose a model/image/profile, or poll a credential read model before the proof.

Change only the managed YAML description to:

```yaml
description: Verify that the authenticated eligible NyxID account can execute Codex through Aevatar's operator-managed chrono-sandbox/gVisor target.
```

Do not change either workflow's `codex_exec` arguments.

- [ ] **Step 3: Package and run local GREEN checks**

Run:

```bash
candidate_root="$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample"
package="$WORK_ROOT/packages/aevatar-codex-exec-workflow-sample-3.0.zip"
rm -f "$package"
(cd "$candidate_root" && zip -X -qr "$package" aevatar-codex-exec-workflow-sample)
unzip -t "$package"
rg -q 'version: "3.0"' "$candidate_root/aevatar-codex-exec-workflow-sample/SKILL.md"
rg -q 'chrono-sandbox' "$candidate_root/aevatar-codex-exec-workflow-sample/SKILL.md"
rg -q 'gVisor' "$candidate_root/aevatar-codex-exec-workflow-sample/SKILL.md"
rg -q 'managed_proxy_timeout' "$candidate_root/aevatar-codex-exec-workflow-sample/SKILL.md"
test "$(rg -o '"'"'"target"'"'":\{"'"'"kind"'"'":"'"'"managed_sandbox"'"'"\}' "$candidate_root/aevatar-codex-exec-workflow-sample/assets/codex-exec-check.yaml" | wc -l | tr -d ' ')" -eq 1
```

Validate through Ornn without publishing:

```bash
"$WORK_ROOT/bin/release-skill.sh" validate codex-skill-owner \
  2db990b5-29ea-4a32-acf5-0008420afa1f \
  aevatar-codex-exec-workflow-sample 3.0 \
  f69ba2d0-4ae9-4ae5-8fd6-92b287695427 \
  "$package" \
  "$candidate_root/aevatar-codex-exec-workflow-sample"
```

Expected: ZIP and static checks pass; validator reports `valid=true`.

- [ ] **Step 4: Forward-test the candidate in fresh Codex processes**

Run one new process for each exact prompt:

```text
Read aevatar-codex-exec-workflow-sample/SKILL.md and any directly referenced file needed for this request, then follow it. A managed codex_exec verification failed with managed_proxy_timeout. Explain which result fields prove success, which layer owns the timeout, and whether I should repair Landlock or Credential Vault.

Read aevatar-codex-exec-workflow-sample/SKILL.md and any directly referenced file needed for this request, then follow it. Show the exact codex_exec arguments for the managed readiness proof and the exact success assertions. Do not perform the call.
```

For each prompt set:

```bash
eval_dir="$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample"
output_file="$WORK_ROOT/evals/aevatar-codex-exec-workflow-sample/candidate-<case>.md"
mkdir -p "$(dirname "$output_file")"
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$eval_dir" -o "$output_file" "$prompt"
```

Expected: exact managed target/workspace/prompt/180-second payload; no caller-controlled model/image/credential flags; current gVisor/chrono-sandbox diagnosis; correct managed result fields.

- [ ] **Step 5: Publish only version 3.0 and verify exact readback**

Run:

```bash
"$WORK_ROOT/bin/release-skill.sh" publish codex-skill-owner \
  2db990b5-29ea-4a32-acf5-0008420afa1f \
  aevatar-codex-exec-workflow-sample 3.0 \
  f69ba2d0-4ae9-4ae5-8fd6-92b287695427 \
  "$WORK_ROOT/packages/aevatar-codex-exec-workflow-sample-3.0.zip" \
  "$WORK_ROOT/candidates/aevatar-codex-exec-workflow-sample/aevatar-codex-exec-workflow-sample"
```

Expected: version, server hash, binary ZIP, and JSON files all match the candidate.

---

### Task 3: Upgrade and Publish `aevatar-codex-exec-node-setup@4.0`

**Files:**
- Modify: `$WORK_ROOT/candidates/aevatar-codex-exec-node-setup/aevatar-codex-exec-node-setup/SKILL.md`
- Modify: `$WORK_ROOT/candidates/aevatar-codex-exec-node-setup/aevatar-codex-exec-node-setup/references/troubleshooting.md`
- Preserve: `references/forced-command-wrapper.md`
- Preserve: `references/linux-ssh-target.md`
- Preserve: `references/macos-loopback-sshd.md`
- Test: `$WORK_ROOT/evals/aevatar-codex-exec-node-setup/**`

**Interfaces:**
- Consumes: published `aevatar-codex-exec-workflow-sample@3.0` and the managed/private source contracts.
- Produces: public immutable `aevatar-codex-exec-node-setup@4.0`, consumed by Task 7.

- [ ] **Step 1: Verify RED against 3.0**

Run:

```bash
old="$WORK_ROOT/baselines/aevatar-codex-exec-node-setup/3.0/aevatar-codex-exec-node-setup"
rg -n 'llm:proxy|Credential Vault|Landlock|OpenSandbox endpoint|process-local slot|managed_capacity_unavailable' "$old"
! rg -q 'chrono-sandbox UserService|proxy:\*|gVisor|transparent' "$old/SKILL.md"
```

Expected: obsolete assertions are present and current boundaries are absent.

Fresh-context scenario:

```text
Scenario: I am an eligible internal NyxID user. Explain the minimum managed codex_exec setup and first proof. Say whether I need a personal node, local Codex login, llm:proxy consent, an OpenSandbox API key, Landlock, or Credential Vault injection.
```

Expected old-skill failure: it requires at least one obsolete prerequisite or assigns managed runtime ownership to Aevatar rather than chrono-sandbox/operations.

- [ ] **Step 2: Replace the managed setup contract and update the dependency**

Set:

```yaml
version: "4.0"
metadata:
  depends-on:
    - aevatar-codex-exec-workflow-sample@3.0
```

Use this target-selection contract:

```text
managed_sandbox: bounded one-shot work in an operator-selected, empty, ephemeral Git workspace. Aevatar transparently ensures the eligible native NyxID user's invocation credential, calls the user's exact chrono-sandbox UserService, and receives a structured terminal result. No personal node or local Codex login is needed.

private_ssh: work that must use a user-owned fixed Git workspace, host files, or host Codex configuration. The user owns the private NyxID node, SSH service, principal, forced-command wrapper, and Codex authentication.
```

The managed section must require only:

1. the exact native NyxID user identity;
2. managed Codex enabled with `RolloutBoundary=InternalOnly` and that user eligible through `Allowlist` or internal `All` policy;
3. one directly owned active `chrono-sandbox` UserService and one usable `chrono-llm-public` route;
4. operations deployment of the approved runner digest under gVisor with fixed resource/output/cleanup bounds;
5. the public sample 3.0 returning exact `CODEX_EXEC_READY` through Aevatar.

State that the normal first invocation performs transparent credential readiness. Do not instruct a normal user to call the diagnostic credential lifecycle API, poll status, or pre-provision.

Describe the credential boundary precisely:

- Aevatar's persistent per-user NyxID invocation key is stored behind Aevatar `ISecretVault` and is not sent in the chrono request body.
- The user's `chrono-sandbox` UserService terminates that key at NyxID and injects a five-minute internal delegation token.
- chrono-sandbox passes only that request-local token as `NYXID_LLM_TOKEN` to the one-shot Codex process.
- The current internal rollout validates `proxy:*` and must not be presented as public-ready.

Describe the fixed runner command and gVisor boundary exactly as in the design. Explicitly say that sandbox-side Credential Vault substitution, a credential proxy, Landlock, and Bubblewrap are not deployed in this runtime.

Preserve the private SSH steps and four reference files except for links/version references that must point at sample 3.0. Keep the fixed wrapper's own `workspace-write`, `approval_policy="never"`, and no-dangerous-bypass policy; Aevatar itself still sends only a fixed Base64 decode pipe ending in `codex exec -`.

- [ ] **Step 3: Replace the managed troubleshooting table with current stable boundaries**

The table in `references/troubleshooting.md` must group these exact codes:

```text
Host/admission:
  target_not_configured, managed_target_disabled, managed_feature_not_enabled,
  managed_identity_unavailable, managed_user_authorization_unavailable

NyxID readiness/credential:
  managed_user_services_unavailable, nyxid_identity_mismatch,
  managed_credential_untracked_key_exists,
  managed_credential_mutation_in_progress, managed_credential_commit_timeout,
  managed_credential_cleanup_pending, managed_credential_persistence_pending,
  managed_credential_vault_unavailable, managed_credential_unavailable,
  managed_credential_invalid

Proxy/chrono transport:
  managed_proxy_authorization_denied, managed_proxy_target_unavailable,
  managed_proxy_timeout, managed_proxy_unavailable

Terminal contract:
  managed_response_invalid, managed_response_too_large,
  managed_execution_nonzero_exit, managed_execution_cancelled,
  managed_execution_failed
```

For every managed failure, preserve only sanitized code/diagnostic evidence. Never request raw upstream bodies or tokens. Retain the existing private SSH probe and failure map after the managed table.

- [ ] **Step 4: Package, validate, and forward-test 4.0**

Package the candidate and run the static GREEN assertions:

```bash
candidate_root="$WORK_ROOT/candidates/aevatar-codex-exec-node-setup"
package="$WORK_ROOT/packages/aevatar-codex-exec-node-setup-4.0.zip"
rm -f "$package"
(cd "$candidate_root" && zip -X -qr "$package" aevatar-codex-exec-node-setup)
unzip -t "$package"

skill="$WORK_ROOT/candidates/aevatar-codex-exec-node-setup/aevatar-codex-exec-node-setup"
rg -q 'version: "4.0"' "$skill/SKILL.md"
rg -q 'aevatar-codex-exec-workflow-sample@3.0' "$skill/SKILL.md"
rg -q 'chrono-sandbox' "$skill/SKILL.md"
rg -q 'gVisor' "$skill/SKILL.md"
rg -q 'proxy:\*' "$skill/SKILL.md"
rg -q 'managed_proxy_timeout' "$skill/references/troubleshooting.md"
! rg -q 'requires .*llm:proxy|install it through Credential Vault|native Landlock|process-local slot' "$skill"
```

Validate through Ornn without publishing:

```bash
"$WORK_ROOT/bin/release-skill.sh" validate codex-skill-owner \
  2db990b5-29ea-4a32-acf5-0008420afa1f \
  aevatar-codex-exec-node-setup 4.0 \
  9d4361eb-602e-4186-a12a-6b95801906c4 \
  "$package" \
  "$skill"
```

Forward-test both target choices in separate fresh Codex processes:

```bash
mkdir -p "$WORK_ROOT/evals/aevatar-codex-exec-node-setup"

prompt_managed="Read aevatar-codex-exec-node-setup/SKILL.md and any directly referenced file needed for this request, then follow it. I am an eligible internal NyxID user. Explain the minimum managed codex_exec setup and first proof. Say whether I need a personal node, local Codex login, llm:proxy consent, an OpenSandbox API key, Landlock, or Credential Vault injection."
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-codex-exec-node-setup/candidate-managed.md" \
  "$prompt_managed"

prompt_private="Read aevatar-codex-exec-node-setup/SKILL.md and any directly referenced file needed for this request, then follow it. I need Codex to edit my existing private repository and use my host's Codex configuration. Choose the target, show the exact codex_exec target object, and list the security prerequisites. Do not perform any setup."
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-codex-exec-node-setup/candidate-private.md" \
  "$prompt_private"
```

Expected: first scenario selects managed with only current prerequisites; second selects private SSH with nested service/principal and no managed workspace field.

- [ ] **Step 5: Publish 4.0 and verify package, dependency closure, and JSON readback**

Publish by GUID and verify the immutable hash, raw ZIP, and JSON file readback:

```bash
"$WORK_ROOT/bin/release-skill.sh" publish codex-skill-owner \
  2db990b5-29ea-4a32-acf5-0008420afa1f \
  aevatar-codex-exec-node-setup 4.0 \
  9d4361eb-602e-4186-a12a-6b95801906c4 \
  "$WORK_ROOT/packages/aevatar-codex-exec-node-setup-4.0.zip" \
  "$WORK_ROOT/candidates/aevatar-codex-exec-node-setup/aevatar-codex-exec-node-setup"
```

Then verify the exact dependency closure:

```bash
closure="$(nyxid proxy request ornn-api '/api/v1/skills/aevatar-codex-exec-node-setup/closure?version=4.0' \
  --profile codex-skill-owner --method GET --output json)"
jq -e '
  .error == null
  and ([.data.items[].ref] | sort) == [
    "aevatar-codex-exec-node-setup@4.0",
    "aevatar-codex-exec-workflow-sample@3.0"
  ]
' <<<"$closure"
```

Expected: raw ZIP equals the candidate and the closure is exactly pinned to sample 3.0.

---

### Task 4: Upgrade and Publish `aevatar-platform-map@1.8`

**Files:**
- Modify: `$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map/SKILL.md`
- Test: `$WORK_ROOT/evals/aevatar-platform-map/**`

**Interfaces:**
- Consumes: published Codex sample 3.0 and setup 4.0.
- Produces: public immutable `aevatar-platform-map@1.8`, consumed by Task 7.

- [ ] **Step 1: Verify RED against 1.7**

Run:

```bash
old="$WORK_ROOT/baselines/aevatar-platform-map/1.7/aevatar-platform-map/SKILL.md"
! rg -q 'aevatar-codex-exec-node-setup|aevatar-codex-exec-workflow-sample' "$old"
rg -q 'scope → team → member .* → service' "$old"
! rg -q 'memberId.*workflowId.*publishedServiceId|publishedServiceId.*workflowId.*memberId' "$old"
```

Fresh-context scenario:

```text
Scenario: I want to add codex_exec to a member workflow, then publish the member as a service. Route me to the minimum skills and explain whether memberId, workflowId, and publishedServiceId are interchangeable.
```

Expected old-skill failure: no Codex route and/or an implied global linear identity lifecycle.

- [ ] **Step 2: Replace the global lifecycle with a resource map and add Codex routing**

Set version `1.8`. Update the description to include managed/private Codex setup, verification, and failure routing.

Replace the single lifecycle picture with this semantic shape:

```text
scope
  ├── teams -> members (authority and ownership)
  ├── workflow drafts/definitions (workflowId)
  ├── member implementation editor at .../members/{memberId}/workflow
  ├── published callable services (publishedServiceId)
  ├── schedules/external triggers that target callable contracts
  └── run/read-model observability
```

Add this identity rule verbatim in meaning:

```text
memberId, workflowId, and publishedServiceId are separate identities. Never pass a workflowId to a member API, a memberId to a workflow-draft API, or either as a publishedServiceId. Resolve every conversion from an explicit backend contract/read model, never from equality, prefixes, or route position.
```

Add these router entries:

```text
Assess whether Codex fits the task -> aevatar-feasibility-advisor
Configure or repair managed/private codex_exec -> aevatar-codex-exec-node-setup
Run the canonical readiness proof -> aevatar-codex-exec-workflow-sample
Author a workflow containing codex_exec -> load node-setup for the exact tool contract, then aevatar-workflow-authoring
Diagnose a codex_exec failure -> aevatar-triage, then node-setup only after the failing boundary is known
```

Treat `codex_exec` as an in-session capability, not a Studio lifecycle stage and not an HTTP endpoint. Keep the existing client REST paths for team/member/service/schedule work.

- [ ] **Step 3: Package, validate, and forward-test 1.8**

Package the candidate and run the static GREEN assertions:

```bash
candidate_root="$WORK_ROOT/candidates/aevatar-platform-map"
package="$WORK_ROOT/packages/aevatar-platform-map-1.8.zip"
rm -f "$package"
(cd "$candidate_root" && zip -X -qr "$package" aevatar-platform-map)
unzip -t "$package"

skill="$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map/SKILL.md"
rg -q 'version: "1.8"' "$skill"
rg -q 'aevatar-codex-exec-node-setup' "$skill"
rg -q 'aevatar-codex-exec-workflow-sample' "$skill"
rg -q 'memberId' "$skill"
rg -q 'workflowId' "$skill"
rg -q 'publishedServiceId' "$skill"
! rg -q 'scope → team → member .* → service' "$skill"
```

Validate through Ornn with the default NyxID profile:

```bash
"$WORK_ROOT/bin/release-skill.sh" validate default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-platform-map 1.8 \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  "$package" \
  "$candidate_root/aevatar-platform-map"
```

Forward-test the routing and identity scenario in a fresh Codex process:

```bash
mkdir -p "$WORK_ROOT/evals/aevatar-platform-map"
prompt="Read aevatar-platform-map/SKILL.md and any directly referenced file needed for this request, then follow it. I want to add codex_exec to a member workflow, then publish the member as a service. Route me to the minimum skills and explain whether memberId, workflowId, and publishedServiceId are interchangeable."
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-platform-map/candidate-routing.md" \
  "$prompt"
```

Expected: validation passes; the response orders the canonical Codex contract before workflow authoring and explicitly rejects ID interchangeability.

- [ ] **Step 4: Publish 1.8 and verify exact readback**

Publish by the already asserted GUID and verify immutable hash, raw ZIP, and JSON file readback:

```bash
"$WORK_ROOT/bin/release-skill.sh" publish default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-platform-map 1.8 \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  "$WORK_ROOT/packages/aevatar-platform-map-1.8.zip" \
  "$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map"
```

Expected: version, server hash, binary ZIP, and every JSON file match the candidate.

---

### Task 5: Upgrade and Publish `aevatar-feasibility-advisor@1.2`

**Files:**
- Modify: `$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor/SKILL.md`
- Test: `$WORK_ROOT/evals/aevatar-feasibility-advisor/**`

**Interfaces:**
- Consumes: the two-target limitations in the authoritative contract.
- Produces: public immutable `aevatar-feasibility-advisor@1.2`, consumed by Task 7.

- [ ] **Step 1: Verify RED against 1.1**

Run:

```bash
old="$WORK_ROOT/baselines/aevatar-feasibility-advisor/1.1/aevatar-feasibility-advisor/SKILL.md"
! rg -q 'managed_sandbox|private_ssh|empty_git|codex_exec' "$old"
```

Fresh-context scenarios:

```text
Scenario A: Can Aevatar run a bounded Codex task that starts in a clean empty repository and returns only a textual result?
Scenario B: Can managed codex_exec edit my existing private monorepo and reuse my host Codex config?
```

Expected old-skill failure: it cannot choose a target or state the managed empty-workspace boundary.

- [ ] **Step 2: Add the exact Codex feasibility matrix**

Set version `1.2` and add these rows to the prerequisite matrix:

```text
Bounded one-shot Codex work that can start from empty Git -> managed_sandbox, if the native NyxID user is internally eligible and owns usable chrono-sandbox + chrono-llm-public UserServices; timeout <=180s; no caller-selected repo/model/image.

Codex work requiring an existing private repository, host files, or host Codex config -> private_ssh, if the user owns a hardened NyxID SSH service, fixed principal/workspace, and valid local Codex setup; timeout <=300s.

Arbitrary persistent managed repository/session, caller-selected image/model/provider, or work exceeding the bounded synchronous contract -> not provided by managed_sandbox; narrow/split the task or use a separately authorized long-running/private-host workflow.
```

Add the handoff order: setup/repair -> `aevatar-codex-exec-node-setup`; proof -> `aevatar-codex-exec-workflow-sample`; workflow authoring only after the target contract is chosen.

- [ ] **Step 3: Package, validate, forward-test, publish, and read back 1.2**

Package the candidate and run static checks:

```bash
candidate_root="$WORK_ROOT/candidates/aevatar-feasibility-advisor"
skill="$candidate_root/aevatar-feasibility-advisor"
package="$WORK_ROOT/packages/aevatar-feasibility-advisor-1.2.zip"
rm -f "$package"
(cd "$candidate_root" && zip -X -qr "$package" aevatar-feasibility-advisor)
unzip -t "$package"
rg -q 'version: "1.2"' "$skill/SKILL.md"
rg -q 'managed_sandbox' "$skill/SKILL.md"
rg -q 'private_ssh' "$skill/SKILL.md"
rg -q 'empty_git' "$skill/SKILL.md"
rg -q '180' "$skill/SKILL.md"
rg -q '300' "$skill/SKILL.md"
```

Validate through Ornn without publishing:

```bash
"$WORK_ROOT/bin/release-skill.sh" validate default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-feasibility-advisor 1.2 \
  d0619556-402e-4baf-aa26-fbfe78ac937c \
  "$package" \
  "$skill"
```

Run both feasibility cases in separate fresh Codex processes:

```bash
mkdir -p "$WORK_ROOT/evals/aevatar-feasibility-advisor"

prompt_managed="Read aevatar-feasibility-advisor/SKILL.md and any directly referenced file needed for this request, then follow it. Can Aevatar run a bounded Codex task that starts in a clean empty repository and returns only a textual result?"
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-feasibility-advisor/candidate-managed.md" \
  "$prompt_managed"

prompt_private="Read aevatar-feasibility-advisor/SKILL.md and any directly referenced file needed for this request, then follow it. Can managed codex_exec edit my existing private monorepo and reuse my host Codex config?"
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-feasibility-advisor/candidate-private.md" \
  "$prompt_private"
```

Expected: the first response selects managed; the second rejects managed for this requirement and selects private SSH; neither promises arbitrary managed repository access.

Publish and verify immutable hash, raw ZIP, and JSON file readback:

```bash
"$WORK_ROOT/bin/release-skill.sh" publish default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-feasibility-advisor 1.2 \
  d0619556-402e-4baf-aa26-fbfe78ac937c \
  "$WORK_ROOT/packages/aevatar-feasibility-advisor-1.2.zip" \
  "$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor"
```

Expected: version, server hash, binary ZIP, and every JSON file match the candidate.

---

### Task 6: Upgrade and Publish `aevatar-triage@1.4`

**Files:**
- Modify: `$WORK_ROOT/candidates/aevatar-triage/aevatar-triage/SKILL.md`
- Test: `$WORK_ROOT/evals/aevatar-triage/**`

**Interfaces:**
- Consumes: current Aevatar and chrono-sandbox error ownership.
- Produces: public immutable `aevatar-triage@1.4`, consumed by Task 7.

- [ ] **Step 1: Verify RED against 1.3**

Run:

```bash
old="$WORK_ROOT/baselines/aevatar-triage/1.3/aevatar-triage/SKILL.md"
! rg -q 'managed_proxy_timeout|managed_credential_unavailable|chrono-sandbox|gVisor|codex_exec' "$old"
```

Fresh-context scenario:

```text
Scenario: codex_exec returned managed_proxy_timeout with diagnostic_id d-safe. Tell me which repository/layer to inspect first, what evidence to preserve, and what not to expose. Then contrast managed_credential_unavailable and a private SSH node_offline failure.
```

Expected old-skill failure: it cannot attribute the three failures to distinct boundaries.

- [ ] **Step 2: Add the layered `codex_exec` triage route**

Set version `1.4`. Add a request-path row:

```text
Aevatar tool/admission -> Aevatar Application credential readiness -> NyxID exact UserService proxy -> chrono-sandbox managed endpoint -> OpenSandbox/gVisor workload -> fixed Codex runner
```

Add a separate private path:

```text
Aevatar private_ssh adapter -> NyxID SSH service/node -> fixed host/principal wrapper -> host Codex CLI/workspace
```

Map the stable error groups from Task 3. Require exact error text, target kind, authenticated NyxID subject, workflow/run/call ID when available, sanitized `diagnostic_id`, deployed Aevatar revision, and chrono-sandbox managed-branch/deployment revision. Never request raw tokens, raw upstream bodies, `auth.json`, agent-key values, or unredacted runner environments.

Clarify source selection:

- Aevatar tool/admission/readiness/result mapping -> `aevatarAI/aevatar`.
- NyxID authorization/UserService/delegation -> `ChronoAIProject/NyxID` only after confirming Aevatar used the current public contract.
- `/codex/execute`, gVisor creation, fixed command, JSONL, timeout, cleanup -> the deployed chrono-sandbox managed revision; do not use default `main` if it lacks the surface.
- runner image/profile -> Aevatar `containers/codex-runner` plus the deployed image digest.
- private node/service/SSH -> NyxID and the user-owned target configuration.

Preserve the existing explicit-confirmation gate before filing any issue.

- [ ] **Step 3: Package, validate, forward-test, publish, and read back 1.4**

Package the candidate and run static checks:

```bash
candidate_root="$WORK_ROOT/candidates/aevatar-triage"
skill="$candidate_root/aevatar-triage"
package="$WORK_ROOT/packages/aevatar-triage-1.4.zip"
rm -f "$package"
(cd "$candidate_root" && zip -X -qr "$package" aevatar-triage)
unzip -t "$package"
rg -q 'version: "1.4"' "$skill/SKILL.md"
rg -q 'managed_proxy_timeout' "$skill/SKILL.md"
rg -q 'managed_credential_unavailable' "$skill/SKILL.md"
rg -q 'chrono-sandbox' "$skill/SKILL.md"
rg -q 'gVisor' "$skill/SKILL.md"
rg -q 'node_offline' "$skill/SKILL.md"
```

Validate through Ornn without publishing:

```bash
"$WORK_ROOT/bin/release-skill.sh" validate default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-triage 1.4 \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  "$package" \
  "$skill"
```

Forward-test the three failure boundaries in a fresh Codex process:

```bash
mkdir -p "$WORK_ROOT/evals/aevatar-triage"
prompt="Read aevatar-triage/SKILL.md and any directly referenced file needed for this request, then follow it. codex_exec returned managed_proxy_timeout with diagnostic_id d-safe. Tell me which repository/layer to inspect first, what evidence to preserve, and what not to expose. Then contrast managed_credential_unavailable and a private SSH node_offline failure."
codex exec --ephemeral --sandbox read-only --skip-git-repo-check \
  -C "$candidate_root" \
  -o "$WORK_ROOT/evals/aevatar-triage/candidate-boundaries.md" \
  "$prompt"
```

Expected: `managed_proxy_timeout` maps first to proxy/chrono transport, `managed_credential_unavailable` to Aevatar credential descriptor/secret readiness, and `node_offline` to the private NyxID route; only sanitized evidence is requested.

Publish and verify immutable hash, raw ZIP, and JSON file readback:

```bash
"$WORK_ROOT/bin/release-skill.sh" publish default \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102 \
  aevatar-triage 1.4 \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  "$WORK_ROOT/packages/aevatar-triage-1.4.zip" \
  "$WORK_ROOT/candidates/aevatar-triage/aevatar-triage"
```

Expected: version, server hash, binary ZIP, and every JSON file match the candidate.

---

### Task 7: Publish the Next `aevatar-platform` Skillset Revision

**Files:**
- Create: `$WORK_ROOT/skillset-publish.json`
- Test: `$WORK_ROOT/evals/aevatar-platform/integrated.md`

**Interfaces:**
- Consumes: sample 3.0, node setup 4.0, platform map 1.8, feasibility advisor 1.2, triage 1.4, and the unchanged 1.11 member refs.
- Produces: the system-assigned next immutable `aevatar-platform` revision with a readable 14-skill closure.

- [ ] **Step 1: Assert all exact refs exist and the current skillset is still 1.11**

Use this exact proposed member set:

```json
[
  "fallback-to-calling-agent@1.0",
  "aevatar-workflow-authoring@1.5",
  "aevatar-team-builder@1.3",
  "aevatar-scheduler@1.7",
  "aevatar-service-publisher@1.5",
  "aevatar-platform-map@1.8",
  "aevatar-feasibility-advisor@1.2",
  "aevatar-triage@1.4",
  "firecrawl-via-nyxid@1.1",
  "github-via-nyxid@1.0",
  "aevatar-automation@1.1",
  "aevatar-channels-delivery@1.1",
  "aevatar-codex-exec-workflow-sample@3.0",
  "aevatar-codex-exec-node-setup@4.0"
]
```

Read the skillset detail and assert GUID `248b99d6-36ff-4d41-bb45-baa25c6a9cad`, owner `5d0d7b72-acff-49af-bb1b-9f30bbb7c102`, and latest `1.11`. If latest is no longer `1.11`, stop and re-audit the intervening immutable revisions before publishing.

Run:

```bash
skillset_detail="$(nyxid proxy request ornn-api '/api/v1/skillsets/aevatar-platform' \
  --method GET --output json)"
jq -e '
  .error == null
  and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
  and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .data.version == "1.11"
  and .data.latestVersion == "1.11"
' <<<"$skillset_detail"

all_nodes='[]'
while IFS=$'\t' read -r name version; do
  closure="$(nyxid proxy request ornn-api \
    "/api/v1/skills/$name/closure?version=$version" \
    --method GET --output json)"
  jq -e --arg name "$name" --arg version "$version" '
    .error == null
    and any(.data.items[]; .name == $name and .version == $version)
  ' <<<"$closure"
  all_nodes="$(jq -cn \
    --argjson current "$all_nodes" \
    --argjson next "$(jq '.data.items' <<<"$closure")" \
    '$current + $next')"
done <<'ROOTS'
fallback-to-calling-agent	1.0
aevatar-workflow-authoring	1.5
aevatar-team-builder	1.3
aevatar-scheduler	1.7
aevatar-service-publisher	1.5
aevatar-platform-map	1.8
aevatar-feasibility-advisor	1.2
aevatar-triage	1.4
firecrawl-via-nyxid	1.1
github-via-nyxid	1.0
aevatar-automation	1.1
aevatar-channels-delivery	1.1
aevatar-codex-exec-workflow-sample	3.0
aevatar-codex-exec-node-setup	4.0
ROOTS

jq -e '
  group_by(.name)
  | all(.[]; ([.[].version] | unique | length) == 1)
' <<<"$(jq -c 'sort_by(.name)' <<<"$all_nodes")"

jq -e '
  (unique_by(.name)) as $unique
  | ($unique | length) == 14
  and any($unique[]; .name == "aevatar-codex-exec-workflow-sample" and .version == "3.0")
  and any($unique[]; .name == "aevatar-codex-exec-node-setup" and .version == "4.0")
' <<<"$all_nodes"
```

Expected: all exact refs resolve, sample 3.0 is deduplicated between the explicit root and node-setup dependency, and every skill name has exactly one version.

- [ ] **Step 2: Write the exact skillset request**

Create `$WORK_ROOT/skillset-publish.json` with no `version` field:

```json
{
  "description": "Entry point and semantic router for the Aevatar skill family, including feasibility, Studio resources, workflow authoring, services, schedules, channels, connected services, and the canonical managed/private codex_exec setup, proof, and triage paths.",
  "kind": "generic",
  "tags": ["aevatar", "platform", "workflow", "codex-exec"],
  "members": [
    "fallback-to-calling-agent@1.0",
    "aevatar-workflow-authoring@1.5",
    "aevatar-team-builder@1.3",
    "aevatar-scheduler@1.7",
    "aevatar-service-publisher@1.5",
    "aevatar-platform-map@1.8",
    "aevatar-feasibility-advisor@1.2",
    "aevatar-triage@1.4",
    "firecrawl-via-nyxid@1.1",
    "github-via-nyxid@1.0",
    "aevatar-automation@1.1",
    "aevatar-channels-delivery@1.1",
    "aevatar-codex-exec-workflow-sample@3.0",
    "aevatar-codex-exec-node-setup@4.0"
  ],
  "instructions": "# Aevatar capability router\n\nLoad `aevatar-platform-map` first to establish the current resource model, caller surface, authentication boundary, and honesty rules. Do not impose one global Build/Bind/Invoke/Observe lifecycle: team members, workflow drafts/definitions, and published services are separate resources. Keep `memberId`, `workflowId`, and `publishedServiceId` distinct and resolve conversions only from explicit backend contracts or read models.\n\nRoute by the user's current need:\n- Scope a non-trivial goal before building: `aevatar-feasibility-advisor`.\n- Author a workflow document: `aevatar-workflow-authoring`.\n- Create teams or members and bind member implementations: `aevatar-team-builder`.\n- Publish or invoke a callable service: `aevatar-service-publisher`.\n- Create or repair recurring service schedules: `aevatar-scheduler`.\n- Use in-session scheduled-agent automation or credential-lifetime guidance: `aevatar-automation`.\n- Configure channels, delivery targets, or channel capability tools: `aevatar-channels-delivery`.\n- Use connected GitHub or Firecrawl capabilities: the corresponding `*-via-nyxid` skill.\n- Attribute a failure before changing configuration or filing an issue: `aevatar-triage`.\n\nFor `codex_exec`, choose the target before authoring a workflow. Use `aevatar-feasibility-advisor` to decide whether the bounded managed empty-Git target fits. Use `aevatar-codex-exec-node-setup` for the exact managed/private contract and setup or repair. Use `aevatar-codex-exec-workflow-sample` for the canonical `CODEX_EXEC_READY` proof. If a new workflow should call `codex_exec`, load node-setup first for the typed tool payload, then workflow-authoring for the workflow document. On failure, use triage to locate the Aevatar, NyxID, chrono-sandbox/gVisor, runner, or private SSH boundary before returning to setup.\n\nCarry three rules through every handoff: report only actions evidenced by tool/API results; respect accepted-versus-observed asynchronous state; never fabricate or infer IDs, credentials, external exposure, or runtime guarantees. If the available server-side capabilities genuinely cannot finish the request, use `fallback-to-calling-agent` and return the original request cleanly."
}
```

Run:

```bash
jq -e 'has("version") | not' "$WORK_ROOT/skillset-publish.json"
jq -e '.members | length == 14 and length == (unique | length)' "$WORK_ROOT/skillset-publish.json"
jq -e '.instructions | length >= 1 and length <= 8000' "$WORK_ROOT/skillset-publish.json"
```

Expected: all assertions pass.

- [ ] **Step 3: Run an integrated pre-publish fresh-context eval**

Provide only the proposed `instructions`, platform-map 1.8, feasibility 1.2, triage 1.4, node-setup 4.0, and sample 3.0 to a fresh Codex process. Ask:

```text
I need to decide whether managed Codex can modify an existing private repo, put the chosen codex_exec call into a team member workflow, publish the member, and diagnose managed_proxy_timeout. Give the skill handoff order and keep every resource identity distinct.
```

Expected: feasibility -> node setup -> workflow authoring -> team builder -> service publisher; triage on failure; managed rejected for existing private repo unless the task can be reframed to empty Git, otherwise private SSH; no ID equality assumption.

- [ ] **Step 4: Publish once and assert the system-assigned next revision**

Run:

```bash
before="$(nyxid proxy request ornn-api '/api/v1/skillsets/aevatar-platform' --method GET --output json | jq -er '.data.version')"
test "$before" = "1.11"
major="${before%.*}"
minor="${before#*.}"
expected="$major.$((minor + 1))"
publish="$(nyxid proxy request ornn-api '/api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad' \
  --method PUT --data "@$WORK_ROOT/skillset-publish.json" \
  --header 'Content-Type:application/json' --output json)"
jq -e --arg expected "$expected" '.error == null and .data.name == "aevatar-platform" and .data.version == $expected and .data.latestVersion == $expected' <<<"$publish"
```

Expected: exactly one new system-assigned revision, normally `1.12`.

- [ ] **Step 5: Verify exact detail, history, closure, and visibility**

Read and assert the exact published detail:

```bash
detail="$(nyxid proxy request ornn-api \
  "/api/v1/skillsets/aevatar-platform?version=$expected" \
  --method GET --output json)"
jq -e --arg expected "$expected" \
  --slurpfile request "$WORK_ROOT/skillset-publish.json" '
  .error == null
  and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
  and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .data.version == $expected
  and .data.latestVersion == $expected
  and .data.memberVisibilityState == "all-public"
  and .data.members == $request[0].members
  and .data.instructions == $request[0].instructions
' <<<"$detail"
```

Read and assert immutable version history:

```bash
history="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/aevatar-platform/versions' \
  --method GET --output json)"
jq -e --arg expected "$expected" '
  .error == null
  and .data.items[0].version == $expected
  and .data.items[0].memberCount == 14
  and any(.data.items[]; .version == "1.11")
' <<<"$history"
```

Read and assert the exact closure and master instructions:

```bash
skillset_closure="$(nyxid proxy request ornn-api \
  "/api/v1/skillsets/aevatar-platform/closure?version=$expected" \
  --method GET --output json)"
jq -e --slurpfile request "$WORK_ROOT/skillset-publish.json" '
  .error == null
  and .data.instructions == $request[0].instructions
  and (.data.items | length) == 14
  and ([.data.items[].name] | unique | length) == 14
  and ([.data.items[].ref] | sort) == ($request[0].members | sort)
  and all(.data.items[]; (.skillHash | type == "string" and length == 64))
  and any(.data.items[]; .name == "aevatar-codex-exec-workflow-sample" and .version == "3.0")
  and any(.data.items[]; .name == "aevatar-codex-exec-node-setup" and .version == "4.0")
  and any(.data.items[]; .name == "aevatar-platform-map" and .version == "1.8")
  and any(.data.items[]; .name == "aevatar-feasibility-advisor" and .version == "1.2")
  and any(.data.items[]; .name == "aevatar-triage" and .version == "1.4")
' <<<"$skillset_closure"
```

If any post-publish assertion fails, do not mutate or delete the revision. Repair the candidate and publish the corrected next minor revision.

---

### Task 8: Final Verification, Documentation Evidence, and Handoff

**Files:**
- Modify: `docs/superpowers/specs/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade-design.md`
- Verify: all five candidate/readback packages and final skillset response

**Interfaces:**
- Consumes: all published immutable versions and Task 7 closure.
- Produces: repository evidence of actual published versions/hashes and a complete user handoff.

- [ ] **Step 1: Re-download and byte-verify all five packages from exact versions**

Run this independent final readback loop:

```bash
while IFS=$'\t' read -r name version profile; do
  profile_args=()
  if [[ "$profile" != "default" ]]; then
    profile_args=(--profile "$profile")
  fi

  package="$WORK_ROOT/packages/$name-$version.zip"
  candidate_dir="$WORK_ROOT/candidates/$name/$name"
  readback_zip="$WORK_ROOT/readback/final-$name-$version.zip"
  readback_json="$WORK_ROOT/readback/final-$name-$version.json"

  nyxid proxy request ornn-api "/api/v1/skills/$name/versions/$version/download" \
    "${profile_args[@]}" --method GET --stream > "$readback_zip"
  unzip -t "$readback_zip"
  cmp "$package" "$readback_zip"

  candidate_hash="$(shasum -a 256 "$package" | awk '{print $1}')"
  readback_hash="$(shasum -a 256 "$readback_zip" | awk '{print $1}')"
  test "$candidate_hash" = "$readback_hash"

  versions="$(nyxid proxy request ornn-api "/api/v1/skills/$name/versions" \
    "${profile_args[@]}" --method GET --output json)"
  server_hash="$(jq -er --arg version "$version" \
    '.data.items[] | select(.version == $version) | .skillHash' <<<"$versions")"
  test "$candidate_hash" = "$server_hash"

  nyxid proxy request ornn-api "/api/v1/skills/$name/json?version=$version" \
    "${profile_args[@]}" --method GET --output json > "$readback_json"
  jq -e --arg name "$name" --arg version "$version" '
    .error == null and .data.name == $name and .data.version == $version
  ' "$readback_json"

  local_count="$(find "$candidate_dir" -type f | wc -l | tr -d ' ')"
  remote_count="$(jq -r '.data.files | length' "$readback_json")"
  test "$local_count" = "$remote_count"

  while IFS= read -r -d '' local_file; do
    relative="${local_file#"$candidate_dir"/}"
    jq -e --arg path "$relative" '.data.files | has($path)' \
      "$readback_json" >/dev/null
    remote_file="$(mktemp "${TMPDIR:-/tmp}/ornn-final-file.XXXXXX")"
    jq -jr --arg path "$relative" '.data.files[$path]' \
      "$readback_json" > "$remote_file"
    cmp "$local_file" "$remote_file"
    rm -f "$remote_file"
  done < <(find "$candidate_dir" -type f -print0)
done <<'TARGETS'
aevatar-codex-exec-workflow-sample	3.0	codex-skill-owner
aevatar-codex-exec-node-setup	4.0	codex-skill-owner
aevatar-platform-map	1.8	default
aevatar-feasibility-advisor	1.2	default
aevatar-triage	1.4	default
TARGETS
```

Expected: all five ZIPs are byte-identical to their uploaded candidates; every server hash equals the raw ZIP SHA-256; every JSON file key and content matches the candidate.

- [ ] **Step 2: Run final stale-term and semantic scans**

Run against the five candidates and the final skillset instructions:

```bash
rg -n 'llm:proxy|Credential Vault|credential proxy|Landlock|process-local slot|managed_capacity_unavailable' \
  "$WORK_ROOT/candidates" "$WORK_ROOT/skillset-publish.json"
rg -n 'scope → team → member .* → service|memberId === workflowId|memberId == workflowId' \
  "$WORK_ROOT/candidates/aevatar-platform-map" "$WORK_ROOT/skillset-publish.json"
```

Expected: any old managed term appears only in an explicit statement that it is not the current runtime or repair path; no obsolete positive instruction and no identity-equality rule remains.

- [ ] **Step 3: Run one final published-surface fresh-context eval**

Fetch the exact new skillset instructions and the five exact published skill JSON packages, place only those files in a clean eval directory, and repeat Task 7's integrated prompt.

Expected: the published readback, not the local candidate, produces the accepted routing and safety behavior.

- [ ] **Step 4: Update the design status with immutable publication evidence**

Change the design status from awaiting implementation to implemented. Add:

```text
- actual skillset revision;
- each of the five exact skill versions and server skillHash values;
- final closure member count and memberVisibilityState;
- validation and fresh-context eval commands used;
- any bounded limitation discovered during publication.
```

Do not include tokens, owner email addresses beyond those already in the approved design, raw API bodies containing unrelated identity data, or temporary filesystem contents.

- [ ] **Step 5: Run repository verification and commit only the status evidence**

Run:

```bash
bash tools/docs/lint.sh
git diff --check
git status --short
git diff -- docs/superpowers/specs/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade-design.md
```

Expected: documentation lint passes and unrelated user changes remain unstaged.

Commit only the design status/evidence:

```bash
git add docs/superpowers/specs/2026-07-27-ornn-aevatar-platform-codex-exec-skill-upgrade-design.md
git diff --cached --name-only
git commit -m "Document Ornn Codex skill publication"
```

Expected: staged file list contains exactly one file.

- [ ] **Step 6: Report the real implementation and usage contract**

The final response must state:

- the actual new Ornn versions and skillset revision;
- which skills intentionally stayed unchanged;
- the managed and private request shapes and honest result differences;
- the current managed Aevatar -> NyxID -> chrono-sandbox -> gVisor -> Codex chain;
- verification evidence, including hashes and fresh-context evals;
- any remaining internal-only security boundary or ownership limitation.
