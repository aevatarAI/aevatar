# Agent Profile Ornn Skillset Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this plan task-by-task. Use
> `superpowers:writing-skills`, `skill-creator`,
> `superpowers:test-driven-development`, and
> `superpowers:verification-before-completion` for every skill release. Steps
> use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish one new Agent Profile management skill, three focused
semantic upgrades, and one exact 13-member `aevatar-platform` skillset revision
that accurately models the deployed Agent Profile capability boundary.

**Architecture:** Treat the live Aevatar OpenAPI/tool surface as deployment
capability authority, the Phase 1 branch contracts as the intended Profile
semantic authority, and immutable Ornn packages as publication authority. Work
outside the repository from exact downloaded ZIPs, complete RED–GREEN–REFACTOR
and remote readback for one skill before beginning the next, then update the
skillset only after all four exact member versions are public and verified.

**Tech Stack:** Aevatar Agent Profile HTTP and `agent_profiles` contracts,
Ornn `/api/v1` skill and skillset APIs, NyxID CLI proxy transport, Markdown/YAML
skill packages, ZIP/SHA-256 tooling, `jq`, `ruby`, `rg`, and fresh-context Codex
skill evaluations.

## Global Constraints

- Implement the approved design in
  `docs/superpowers/specs/2026-07-27-agent-profile-ornn-skillset-upgrade-design.md`.
- Do not modify Aevatar, NyxID, or Ornn runtime source, configuration, rollout
  admission, Profiles, conversations, teams, members, workflows, services,
  channels, AgentRuns, or schedules.
- The only allowed production writes are one new Ornn skill, three new versions
  on the fixed existing skill GUIDs, one visibility update for the newly
  created skill, and one new revision on the fixed skillset GUID.
- Preserve every unrelated dirty or staged worktree change. All ZIPs, response
  bodies, evaluation outputs, and helper scripts live below
  `${TMPDIR:-/tmp}/aevatar-agent-profile-ornn-upgrade-2026-07-27`.
- At the start of every Task and every new execution shell, run
  `export WORK_ROOT="${TMPDIR:-/tmp}/aevatar-agent-profile-ornn-upgrade-2026-07-27"`.
  Do not assume Task 1's shell environment survives a subagent, checkpoint, or
  later `exec`; every helper fails closed when `WORK_ROOT` is absent.
- Use `apply_patch` for authored text. `unzip`, `cp`, and `zip` are permitted
  only for mechanical exact-package extraction, preservation, and repacking;
  `git show <fixed-commit>:<path>` is permitted only to extract the immutable
  Phase 1 management-skill source pinned below.
- Never print, copy, package, or commit a NyxID token, Ornn credential,
  authorization header, refresh token, cookie, local profile file, or raw
  production response dump.
- The required Ornn owner/publisher is exactly
  `5d0d7b72-acff-49af-bb1b-9f30bbb7c102`. Stop before every write if the
  authenticated identity or remote resource owner differs.
- A NyxID CLI process exit code is not evidence of a downstream 2xx. Parse
  every JSON success envelope with `jq`; on binary reads, verify ZIP structure,
  exact version metadata, and SHA-256.
- Use GUID plus literal `<major>.<minor>` for immutable skill reads and writes.
  Never use `latest`, a dist-tag, a version range, or a name-only authority
  reference in an Agent Profile skill binding.
- New skill `aevatar-agent-profile-management` starts at `1.0` only if the live
  name is still absent. Existing planned targets are
  `aevatar-platform-map@1.8`, `aevatar-feasibility-advisor@1.2`, and
  `aevatar-triage@1.4` only if their exact live predecessors and hashes still
  match this plan.
- If another release has moved a predecessor version, hash, owner, visibility,
  or the `aevatar-platform@1.11` composition, stop. Download the new immutable
  predecessor, reconcile its semantics with this design, rerun RED–GREEN–
  REFACTOR, and obtain review of the new literal targets before publishing.
- Complete baseline evaluation, candidate evaluation, hardening evaluation,
  local inspection, live format validation, concurrency reread, publication,
  exact JSON readback, exact ZIP readback, and public-visibility verification
  for one skill before touching the next skill.
- Fresh-context agents are evaluation subjects. Give them only the exact old or
  candidate skill plus the realistic task; do not give them this plan, the
  approved design, expected answer, scoring result, or prior conclusions.
- The generic `skill-creator` quick validator is not a release gate because it
  rejects Ornn's required top-level `version` field. Use the local Ornn-aware
  validator in Task 1 and the live Ornn Zod validator as the complete format
  authority.
- The authoritative management-skill source is the Git object
  `89e57eb5dc7011fe1c092f2f94193c5059ecb72a:skills/aevatar-agent-profile-management/SKILL.md`
  on Phase 1, with SHA-256
  `9519d90cb5e4207581e943c691f487c5923f90ec5eb841725a204416e1cf1977`.
  Use that exact object as the semantic baseline, never the mutable worktree.
  Task 2's complete candidate is the audited external Ornn release projection:
  it may expand the Ornn discovery trigger, add package metadata,
  REST/deployment capability guidance, and current runtime boundaries, but it
  must preserve every source invariant through the executable
  source-to-candidate contract mirror. Preserve both source and diff as
  temporary evidence; do not modify or publish from the Phase 1 worktree file.
- Agent Profile identities remain isolated: `profileId`, `workflowId`,
  `memberId`, `publishedServiceId`, and conversation Actor id are never aliases
  and are never converted by string equality, prefix, route position, or
  lifecycle inference.
- Agent Profile management is capability-detected. In-session mode requires
  the exact `agent_profiles` tool. REST mode requires all eight method/path
  pairs in the live `/api/openapi.json` before any mutation.
- A Profile-resource `404` is not a deployment probe. After the capability
  probe succeeds, it means the requested Profile is missing or invisible.
- Versioned Profile mutations require the strong ETag from the owner management
  read and `If-Match`. A REST transport must both expose the downstream ETag
  response header and forward the caller's `If-Match` request header. The
  current generic NyxID CLI prints only the response body, and the checked
  NyxID generic-proxy request-header allowlist omits `If-Match`; that path is
  capability-probe/read/proposal-only for Profile work, not a mutation client.
- `202 Accepted` means accepted for dispatch only. Publication is proven only
  by owner-management reread evidence: operation reconciliation, authority
  state/version, published revision, source draft digest, and snapshot digest.
- Current Profile execution consumption is limited to Host-rollout-selected,
  newly created NyxID direct conversations. Existing conversations do not
  hot-upgrade. Workflow, Studio, relay, Channel, Scheduled, AgentRun, arbitrary
  services, and schedules are not current Profile consumers.
- Profile publication never creates or binds a workflow, team, member, service,
  schedule, channel, AgentRun, or conversation.
- Tool policies only narrow authority. `ALWAYS` has no routing policy;
  `ROUTED` and `DEFAULT_FOR_UNMATCHED_TURN` require the complete typed routing
  policy, and task/recovery policies must be subsets of the Profile maximum.
- Ornn skill versions and skillset revisions are immutable. Never overwrite,
  delete, deprecate, or mutate a prior rollback version during this release.
- Do not push a branch, open a PR, enable plugin export, or change any existing
  skill visibility without separate user authority.

---

## Fixed Baseline

| Resource | GUID | Owner | Old | Target | Old SHA-256 |
| --- | --- | --- | ---: | ---: | --- |
| `aevatar-platform-map` | `b8bf9e98-2658-4e09-9c51-2e4958137091` | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | `1.7` | `1.8` | `10ec2384b9c200594d20a0483237e8d0f77b56f917e802fe38fed93b904ac683` |
| `aevatar-feasibility-advisor` | `d0619556-402e-4baf-aa26-fbfe78ac937c` | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | `1.1` | `1.2` | `a8ddcf50cffa3d4577a6a2624660599115f294ec6f9770222c233d9ad82341a4` |
| `aevatar-triage` | `fbd40315-317f-4f80-9885-b44b83e1a204` | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | `1.3` | `1.4` | `4ac7270d401e5245e9fb762d551120e5628436ef159d1cdc187b4ef4b0c3d388` |
| `aevatar-agent-profile-management` | assigned on create | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | absent | `1.0` | n/a |
| `aevatar-platform` | `248b99d6-36ff-4d41-bb45-baa25c6a9cad` | `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | `1.11` | server-assigned next revision | n/a |

The Phase 1 management-skill authority pin is:

| Source | Commit | Path | SHA-256 |
| --- | --- | --- | --- |
| local Phase 1 Git object | `89e57eb5dc7011fe1c092f2f94193c5059ecb72a` | `skills/aevatar-agent-profile-management/SKILL.md` | `9519d90cb5e4207581e943c691f487c5923f90ec5eb841725a204416e1cf1977` |

The nine unchanged skillset pins are:

```text
fallback-to-calling-agent@1.0
aevatar-workflow-authoring@1.5
aevatar-team-builder@1.3
aevatar-scheduler@1.7
aevatar-service-publisher@1.5
firecrawl-via-nyxid@1.1
github-via-nyxid@1.0
aevatar-automation@1.1
aevatar-channels-delivery@1.1
```

## File Map

Repository file:

- `docs/superpowers/plans/2026-07-27-agent-profile-ornn-skillset-upgrade.md`:
  this executable plan. No runtime source file changes are part of the release.

Temporary release files:

- `$WORK_ROOT/baselines/<name>/<version>/<name>/...`: exact immutable old
  package extracted after hash verification.
- `$WORK_ROOT/candidates/<name>/<name>/...`: candidate copied from the exact
  old package; the new management candidate is copied from the fixed Phase 1
  Git object and receives only the audited Ornn release delta.
- `$WORK_ROOT/baselines/aevatar-agent-profile-management/source/SKILL.md`:
  byte-exact fixed-commit Phase 1 authority source.
- `$WORK_ROOT/packages/<name>-<version>.zip`: exact bytes submitted to Ornn.
- `$WORK_ROOT/readback/<name>-<version>.zip`: exact bytes downloaded by GUID
  and literal version after publication.
- `$WORK_ROOT/readback/<name>-<version>.release.json`: non-sensitive exact
  GUID/owner/version/hash release evidence emitted by the release gate.
- `$WORK_ROOT/baselines/aevatar-platform-1.11-closure.json`: normalized exact
  12-member predecessor closure used for executable composition comparison.
- `$WORK_ROOT/evals/<name>/<scenario>/<variant>.md`: temporary raw no-skill,
  old-skill, candidate, and hardened-candidate evaluation answers.
- `$WORK_ROOT/bin/validate-ornn-skill.rb`: deterministic local Ornn frontmatter
  and package-layout validator.
- `$WORK_ROOT/bin/validate-agent-profile-management-projection.rb`: verifies
  the external Ornn management candidate against the fixed Phase 1 authority
  source and the real closed `agent_profiles` contract.
- `$WORK_ROOT/bin/release-ornn-skill.sh`: validation, concurrency, publication,
  visibility, and exact readback gate for one skill.
- `$WORK_ROOT/bin/run-skill-eval.sh`: persistent fresh-context evaluator used
  across task/subagent shell boundaries.
- `$WORK_ROOT/bin/run-agent-profile-management-suite.sh`: ten-scenario Profile
  management suite parameterized by skill source and result variant.
- `$WORK_ROOT/bin/run-platform-map-suite.sh`: four-scenario router suite.
- `$WORK_ROOT/bin/run-feasibility-advisor-suite.sh`: four-scenario feasibility
  suite.
- `$WORK_ROOT/bin/run-triage-suite.sh`: six-scenario diagnostics suite.
- `$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh`: eight-scenario
  exact-readback integration suite.
- `$WORK_ROOT/aevatar-platform-master.md`: exact skillset master instructions.

---

### Task 1: Establish the Immutable Baseline and Release Harness

**Files:**

- Create: `$WORK_ROOT/baselines/**`
- Create: `$WORK_ROOT/candidates/**`
- Create: `$WORK_ROOT/packages/**`
- Create: `$WORK_ROOT/readback/**`
- Create: `$WORK_ROOT/evals/**`
- Create: `$WORK_ROOT/bin/validate-ornn-skill.rb`
- Create: `$WORK_ROOT/bin/release-ornn-skill.sh`
- Create: `$WORK_ROOT/bin/run-skill-eval.sh`

**Interfaces:**

- Consumes: the fixed baseline table and current authenticated NyxID profile.
- Produces: byte-verified predecessor packages and deterministic gates used by
  Tasks 2–7.

- [ ] **Step 1: Create a repository-external workspace and assert identity**

Run:

```bash
export WORK_ROOT="${TMPDIR:-/tmp}/aevatar-agent-profile-ornn-upgrade-2026-07-27"
mkdir -p \
  "$WORK_ROOT/bin" \
  "$WORK_ROOT/baselines" \
  "$WORK_ROOT/candidates" \
  "$WORK_ROOT/packages" \
  "$WORK_ROOT/readback" \
  "$WORK_ROOT/evals"

case "$WORK_ROOT" in
  "${TMPDIR:-/tmp}"/aevatar-agent-profile-ornn-upgrade-2026-07-27) ;;
  *) echo "unsafe WORK_ROOT: $WORK_ROOT" >&2; exit 64 ;;
esac

git -C /Users/eanzhao/Code/aevatar status --short --branch

identity="$(nyxid whoami --output json)"
jq -e '.id == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"' \
  <<<"$identity" >/dev/null

ornn_identity="$(nyxid proxy request ornn-api '/api/v1/me' \
  --method GET --output json)"
jq -e '
  .error == null
  and .data.userId == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and (.data.permissions | index("ornn:skill:create")) != null
  and (.data.permissions | index("ornn:skill:update")) != null
  and (.data.permissions | index("ornn:skill:read")) != null
' <<<"$ornn_identity" >/dev/null
```

Expected: all assertions exit 0; existing repository changes remain untouched;
no command prints a credential.

- [ ] **Step 2: Prove the new skill name remains unallocated**

Run:

```bash
probe="$(nyxid proxy request ornn-api \
  '/api/v1/skills/aevatar-agent-profile-management' \
  --method GET --output json 2>/dev/null)"
jq -e '.status == 404 and .code == "skill_not_found"' \
  <<<"$probe" >/dev/null
```

Expected: the exact name returns Ornn's typed `skill_not_found`. If it returns
a skill, stop and verify its owner, GUID, version, hash, package, and semantics;
do not create a duplicate or assume ownership.

- [ ] **Step 3: Download the three exact predecessor ZIPs**

Run:

```bash
while IFS=$'\t' read -r name version guid expected_hash; do
  detail="$(nyxid proxy request ornn-api "/api/v1/skills/$guid?version=$version" \
    --method GET --output json)"
  jq -e \
    --arg name "$name" \
    --arg version "$version" \
    --arg guid "$guid" \
    --arg owner "5d0d7b72-acff-49af-bb1b-9f30bbb7c102" \
    --arg hash "$expected_hash" '
      .error == null
      and .data.name == $name
      and .data.version == $version
      and .data.guid == $guid
      and .data.createdBy == $owner
      and .data.isPrivate == false
      and .data.skillHash == $hash
    ' <<<"$detail" >/dev/null

  mkdir -p "$WORK_ROOT/baselines/$name/$version"
  nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/versions/$version/download" \
    --method GET --stream \
    > "$WORK_ROOT/baselines/$name/$version.zip"
  unzip -t "$WORK_ROOT/baselines/$name/$version.zip" >/dev/null
  actual_hash="$(shasum -a 256 "$WORK_ROOT/baselines/$name/$version.zip" | awk '{print $1}')"
  test "$actual_hash" = "$expected_hash"
  unzip -q "$WORK_ROOT/baselines/$name/$version.zip" \
    -d "$WORK_ROOT/baselines/$name/$version"
  test -f "$WORK_ROOT/baselines/$name/$version/$name/SKILL.md"

  mkdir -p "$WORK_ROOT/candidates/$name"
  cp -R "$WORK_ROOT/baselines/$name/$version/$name" \
    "$WORK_ROOT/candidates/$name/$name"
done <<'BASELINES'
aevatar-platform-map	1.7	b8bf9e98-2658-4e09-9c51-2e4958137091	10ec2384b9c200594d20a0483237e8d0f77b56f917e802fe38fed93b904ac683
aevatar-feasibility-advisor	1.1	d0619556-402e-4baf-aa26-fbfe78ac937c	a8ddcf50cffa3d4577a6a2624660599115f294ec6f9770222c233d9ad82341a4
aevatar-triage	1.3	fbd40315-317f-4f80-9885-b44b83e1a204	4ac7270d401e5245e9fb762d551120e5628436ef159d1cdc187b4ef4b0c3d388
BASELINES
```

Expected: all three exact ZIP hashes match the server's immutable version
hashes and each package contains exactly its original files before editing.

- [ ] **Step 4: Snapshot and assert the exact skillset predecessor**

Run:

```bash
skillset="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/aevatar-platform?version=1.11' \
  --method GET --output json)"
jq -e '
  .error == null
  and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
  and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .data.version == "1.11"
  and .data.latestVersion == "1.11"
  and .data.kind == "generic"
  and .data.memberVisibilityState == "all-public"
  and .data.unreadableMembers == []
  and .data.members == [
    "fallback-to-calling-agent@1.0",
    "aevatar-workflow-authoring@1.5",
    "aevatar-team-builder@1.3",
    "aevatar-scheduler@1.7",
    "aevatar-service-publisher@1.5",
    "aevatar-platform-map@1.7",
    "aevatar-feasibility-advisor@1.1",
    "aevatar-triage@1.3",
    "firecrawl-via-nyxid@1.1",
    "github-via-nyxid@1.0",
    "aevatar-automation@1.1",
    "aevatar-channels-delivery@1.1"
  ]
' <<<"$skillset" >/dev/null

closure="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/aevatar-platform/closure?version=1.11' \
  --method GET --output json)"
jq -e '
  .error == null
  and (.data.items | length) == 12
  and all(.data.items[]; .depth == 0)
' <<<"$closure" >/dev/null

jq -e '[.data.items[] | {
  ref,
  name,
  guid,
  version,
  skillHash,
  depth
}]' <<<"$closure" \
  > "$WORK_ROOT/baselines/aevatar-platform-1.11-closure.json"
```

Expected: exact predecessor identity, composition, public visibility, and
12-entry direct closure all pass. Any difference stops the release.

- [ ] **Step 5: Create the Ornn-aware local validator**

Use `apply_patch` to add `$WORK_ROOT/bin/validate-ornn-skill.rb` with this exact
content:

```ruby
#!/usr/bin/env ruby
require "yaml"

dir = File.expand_path(ARGV.fetch(0))
expected_name = ARGV.fetch(1)
expected_version = ARGV.fetch(2)
path = File.join(dir, "SKILL.md")
abort("SKILL.md missing") unless File.file?(path)

text = File.read(path, encoding: "UTF-8")
match = text.match(/\A---\n(.*?)\n---\n/m) or abort("frontmatter missing")
frontmatter = YAML.safe_load(match[1], permitted_classes: [], aliases: false)
abort("frontmatter must be a mapping") unless frontmatter.is_a?(Hash)

allowed = %w[name description version license compatibility metadata]
unknown = frontmatter.keys - allowed
abort("unexpected frontmatter keys: #{unknown.sort.join(',')}") unless unknown.empty?
abort("name mismatch") unless frontmatter["name"] == expected_name
abort("version mismatch") unless frontmatter["version"] == expected_version
abort("invalid description") unless frontmatter["description"].is_a?(String) &&
  !frontmatter["description"].strip.empty?

metadata = frontmatter["metadata"]
abort("metadata missing") unless metadata.is_a?(Hash)
category = metadata["category"]
abort("unsupported category") unless %w[plain tool-based].include?(category)

tags = metadata.fetch("tag", [])
abort("invalid tags") unless tags.is_a?(Array) && tags.length <= 10 &&
  tags.all? { |tag| tag.is_a?(String) && tag.match?(/\A[a-z0-9-]+\z/) }

tools = metadata.fetch("tool-list", [])
if category == "plain"
  abort("plain skill cannot declare tool-list") unless tools.empty?
else
  abort("tool-based skill requires tool-list") unless tools.is_a?(Array) &&
    !tools.empty? && tools.all? { |tool| tool.is_a?(String) && !tool.strip.empty? }
end

entries = Dir.chdir(File.dirname(dir)) do
  Dir.glob("#{File.basename(dir)}/**/*", File::FNM_DOTMATCH)
    .reject { |entry| entry.end_with?("/.", "/..") }
end
abort("symlink forbidden") if entries.any? { |entry| File.symlink?(File.join(File.dirname(dir), entry)) }
abort("finder metadata forbidden") if entries.any? { |entry| File.basename(entry) == ".DS_Store" }
abort("nested package forbidden") if entries.any? { |entry| entry.end_with?(".zip") }

puts({ name: expected_name, version: expected_version, category: category, files: entries.length }.inspect)
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/validate-ornn-skill.rb"
ruby -c "$WORK_ROOT/bin/validate-ornn-skill.rb"
```

Expected: `Syntax OK`.

- [ ] **Step 6: Create the deterministic release gate**

Use `apply_patch` to add `$WORK_ROOT/bin/release-ornn-skill.sh` with this exact
content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 9 ]]; then
  echo "usage: $0 <validate|publish> <create|update> <name> <target-version> <guid-or-dash> <previous-version-or-dash> <previous-hash-or-dash> <candidate-dir> <expected-owner>" >&2
  exit 64
fi

mode="$1"
kind="$2"
name="$3"
target_version="$4"
guid="$5"
previous_version="$6"
previous_hash="$7"
candidate_dir="$8"
expected_owner="$9"

[[ "$mode" == "validate" || "$mode" == "publish" ]]
[[ "$kind" == "create" || "$kind" == "update" ]]
test -n "${WORK_ROOT:-}"
test -f "$candidate_dir/SKILL.md"

"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate_dir" "$name" "$target_version" >/dev/null

if find "$candidate_dir" -type f \( \
  -name '.env' -o -name '*.pem' -o -name '*.key' -o -name '*.p12' -o \
  -name 'access_token' -o -name 'refresh_token' -o -name '*.zip' \
\) | grep -q .; then
  echo "forbidden package file" >&2
  exit 65
fi

if rg -n \
  'Authorization:[[:space:]]*Bearer[[:space:]]+[A-Za-z0-9._-]{20,}|-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----|"refresh_token"[[:space:]]*:[[:space:]]*"[^" ]{12,}"' \
  "$candidate_dir" >/dev/null; then
  echo "credential-shaped content found" >&2
  exit 65
fi

package="$WORK_ROOT/packages/$name-$target_version.zip"
rm -f "$package"
(
  cd "$(dirname "$candidate_dir")"
  COPYFILE_DISABLE=1 zip -X -q -r "$package" "$name"
)
unzip -t "$package" >/dev/null
test "$(unzip -Z1 "$package" | sed -n '1p' | cut -d/ -f1)" = "$name"
candidate_hash="$(shasum -a 256 "$package" | awk '{print $1}')"

validation="$(nyxid proxy request ornn-api '/api/v1/skill-format/validate' \
  --method POST \
  --header 'Content-Type:application/zip' \
  --data "@$package" \
  --output json)"
jq -e '
  .error == null
  and .data.valid == true
  and ((.data.violations // []) | length) == 0
' <<<"$validation" >/dev/null

identity="$(nyxid proxy request ornn-api '/api/v1/me' --method GET --output json)"
jq -e \
  --arg owner "$expected_owner" \
  --arg required_permission "$([[ "$kind" == "create" ]] && printf %s "ornn:skill:create" || printf %s "ornn:skill:update")" '
  .error == null
  and .data.userId == $owner
  and (.data.permissions | index("ornn:skill:read")) != null
  and (.data.permissions | index($required_permission)) != null
  and (.data.permissions | index("ornn:skill:update")) != null
' <<<"$identity" >/dev/null

if [[ "$kind" == "create" ]]; then
  [[ "$guid" == "-" && "$previous_version" == "-" && "$previous_hash" == "-" ]]
  probe="$(nyxid proxy request ornn-api "/api/v1/skills/$name" \
    --method GET --output json 2>/dev/null)"
  jq -e '.status == 404 and .code == "skill_not_found"' <<<"$probe" >/dev/null
else
  [[ "$guid" != "-" && "$previous_version" != "-" && "$previous_hash" != "-" ]]
  before="$(nyxid proxy request ornn-api "/api/v1/skills/$guid" \
    --method GET --output json)"
  jq -e \
    --arg owner "$expected_owner" \
    --arg guid "$guid" \
    --arg name "$name" \
    --arg version "$previous_version" \
    --arg hash "$previous_hash" '
      .error == null
      and .data.createdBy == $owner
      and .data.guid == $guid
      and .data.name == $name
      and .data.version == $version
      and .data.skillHash == $hash
      and .data.isPrivate == false
    ' <<<"$before" >/dev/null

  versions="$(nyxid proxy request ornn-api "/api/v1/skills/$guid/versions" \
    --method GET --output json)"
  jq -e --arg target "$target_version" '
    .error == null
    and ([.data.items[] | select(.version == $target)] | length) == 0
  ' <<<"$versions" >/dev/null
fi

if [[ "$mode" == "validate" ]]; then
  jq -n \
    --arg name "$name" \
    --arg version "$target_version" \
    --arg hash "$candidate_hash" \
    '{stage:"validated",name:$name,version:$version,skillHash:$hash}'
  exit 0
fi

if [[ "$kind" == "create" ]]; then
  published="$(nyxid proxy request ornn-api '/api/v1/skills' \
    --method POST \
    --header 'Content-Type:application/zip' \
    --data "@$package" \
    --output json)"
  guid="$(jq -er \
    --arg owner "$expected_owner" \
    --arg name "$name" \
    --arg version "$target_version" \
    --arg hash "$candidate_hash" '
      select(
        .error == null
        and .data.createdBy == $owner
        and .data.name == $name
        and .data.version == $version
        and .data.skillHash == $hash
        and .data.isPrivate == true
      ) | .data.guid
    ' <<<"$published")"

  visibility="$(nyxid proxy request ornn-api "/api/v1/skills/$guid/permissions" \
    --method PUT \
    --header 'Content-Type: application/json' \
    --data '{"isPrivate":false,"sharedWithUsers":[],"sharedWithOrgs":[]}' \
    --output json)"
  jq -e \
    --arg guid "$guid" \
    --arg owner "$expected_owner" '
      .error == null
      and .data.skill.guid == $guid
      and .data.skill.createdBy == $owner
      and .data.skill.isPrivate == false
    ' <<<"$visibility" >/dev/null
else
  published="$(nyxid proxy request ornn-api "/api/v1/skills/$guid" \
    --method PUT \
    --header 'Content-Type:application/zip' \
    --data "@$package" \
    --output json)"
  jq -e \
    --arg guid "$guid" \
    --arg owner "$expected_owner" \
    --arg name "$name" \
    --arg version "$target_version" \
    --arg hash "$candidate_hash" '
      .error == null
      and .data.guid == $guid
      and .data.createdBy == $owner
      and .data.name == $name
      and .data.version == $version
      and .data.skillHash == $hash
      and .data.isPrivate == false
    ' <<<"$published" >/dev/null
fi

detail="$(nyxid proxy request ornn-api "/api/v1/skills/$guid?version=$target_version" \
  --method GET --output json)"
jq -e \
  --arg guid "$guid" \
  --arg owner "$expected_owner" \
  --arg name "$name" \
  --arg version "$target_version" \
  --arg hash "$candidate_hash" '
    .error == null
    and .data.guid == $guid
    and .data.createdBy == $owner
    and .data.name == $name
    and .data.version == $version
    and .data.skillHash == $hash
    and .data.isPrivate == false
  ' <<<"$detail" >/dev/null

json="$(nyxid proxy request ornn-api "/api/v1/skills/$guid/json?version=$target_version" \
  --method GET --output json)"
jq -e --arg name "$name" --arg version "$target_version" '
  .error == null
  and .data.name == $name
  and .data.version == $version
  and (.data.files | type) == "object"
' <<<"$json" >/dev/null

local_count="$(find "$candidate_dir" -type f | wc -l | tr -d ' ')"
remote_count="$(jq -r '.data.files | length' <<<"$json")"
test "$local_count" = "$remote_count"
while IFS= read -r -d '' local_file; do
  relative="${local_file#"$candidate_dir"/}"
  jq -e --arg path "$relative" '.data.files | has($path)' <<<"$json" >/dev/null
  remote_file="$(mktemp "${TMPDIR:-/tmp}/ornn-readback-file.XXXXXX")"
  jq -jr --arg path "$relative" '.data.files[$path]' <<<"$json" > "$remote_file"
  cmp "$local_file" "$remote_file"
  rm -f "$remote_file"
done < <(find "$candidate_dir" -type f -print0)

readback="$WORK_ROOT/readback/$name-$target_version.zip"
nyxid proxy request ornn-api \
  "/api/v1/skills/$guid/versions/$target_version/download" \
  --method GET --stream > "$readback"
unzip -t "$readback" >/dev/null
test "$(shasum -a 256 "$readback" | awk '{print $1}')" = "$candidate_hash"
cmp "$package" "$readback"

jq -n \
  --arg guid "$guid" \
  --arg owner "$expected_owner" \
  --arg name "$name" \
  --arg version "$target_version" \
  --arg hash "$candidate_hash" \
  '{stage:"published-and-verified",guid:$guid,owner:$owner,name:$name,version:$version,skillHash:$hash,isPrivate:false}'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/release-ornn-skill.sh"
bash -n "$WORK_ROOT/bin/release-ornn-skill.sh"
```

Expected: syntax validation exits 0. Do not invoke `publish` until the
corresponding skill task has completed all forward tests.

- [ ] **Step 7: Create the persistent fresh-context evaluator and scoring contract**

Use `apply_patch` to create `$WORK_ROOT/bin/run-skill-eval.sh` with this exact
content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 5 ]]; then
  echo "usage: $0 <skill-name> <scenario> <variant> <source-dir-or-dash> <prompt>" >&2
  exit 64
fi

skill_name="$1"
scenario="$2"
variant="$3"
source_dir="$4"
prompt="$5"

test -n "${WORK_ROOT:-}"
[[ "$skill_name" =~ ^[a-z0-9-]+$ ]]
[[ "$scenario" =~ ^[a-z0-9-]+$ ]]
[[ "$variant" =~ ^[a-z0-9-]+$ ]]

eval_dir="$WORK_ROOT/evals/$skill_name/$scenario/$variant-input"
output="$WORK_ROOT/evals/$skill_name/$scenario/$variant.md"
case "$eval_dir" in
  "$WORK_ROOT"/evals/*/*/*-input) ;;
  *) echo "unsafe eval dir: $eval_dir" >&2; exit 64 ;;
esac

rm -rf -- "$eval_dir"
mkdir -p "$eval_dir" "$(dirname "$output")"
if [[ "$source_dir" != "-" ]]; then
  test -f "$source_dir/SKILL.md"
  cp -R "$source_dir" "$eval_dir/$skill_name"
  prompt="Read $skill_name/SKILL.md completely and follow it for this request. $prompt"
fi

codex exec --ephemeral --ignore-user-config --ignore-rules \
  --sandbox read-only --skip-git-repo-check \
  -C "$eval_dir" \
  -o "$output" \
  "$prompt"
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-skill-eval.sh"
bash -n "$WORK_ROOT/bin/run-skill-eval.sh"
```

Expected: syntax check exits 0. Each invocation creates a clean, isolated
input directory and a persistent raw answer, even when Tasks 2–5 run in new
subagent shells.

Score every raw answer against these exact rules:

| ID | Required evidence | Automatic failure |
| --- | --- | --- |
| `C1` | Detect exact tool or complete OpenAPI family before mutation. | Calls or claims a Profile mutation without detection. |
| `C2` | Keeps `profileId`, `wf-alpha`, `m-alpha`, `svc-alpha`, and conversation id separate. | Treats any two as aliases or sends one identity to another resource API. |
| `C3` | Uses GUID, literal version, expected name, and expected publisher from an authoritative Ornn exact-detail read; search is discovery only. | Uses name-only, search output as authority, `latest`, a range, dist-tag, inferred publisher, or inline body. |
| `C4` | `ALWAYS` omits routing policy; routed/default includes all five typed routing fields and classifies the selected skill's real effect (`firecrawl-via-nyxid` is `SERVICE_CALL`). | Wrong routing-policy shape, mode rule, or side-effect class. |
| `C5` | Requires ETag/`If-Match`; on `412`, rereads and reconstructs. | Blind replay or mutation without the current strong validator. |
| `C6` | Describes `202` as dispatch accepted and names reread proof. | Calls it committed, published, visible, or bound. |
| `C7` | Limits consumption to Host-selected new NyxID direct conversations. | Promises Workflow, Studio, relay, Channel, Scheduled, AgentRun, arbitrary services, schedules, or existing chats consume it. |
| `C8` | After successful capability detection, interprets Profile `404` as missing/invisible. | Uses every Profile `404` as proof the contract is undeployed. |
| `C9` | Stops when capability is absent and may only draft a proposal. | Falls back to workflow/team/member/service/schedule creation. |
| `C10` | Tool and recovery policies narrow authority. | Claims a Profile or skill grants credentials, scopes, tools, or services. |

Baseline succeeds as RED only when at least one assigned rule fails and the raw
answer contains the model's own rationale. Candidate/hardening succeeds only
when every assigned rule passes. Keep raw answers temporary; report only rule
IDs and short non-sensitive excerpts in the final implementation handoff.

---

### Task 2: Author, Test, and Publish `aevatar-agent-profile-management@1.0`

**Files:**

- Create: `$WORK_ROOT/baselines/aevatar-agent-profile-management/source/SKILL.md`
- Create: `$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management/SKILL.md`
- Create: `$WORK_ROOT/bin/validate-agent-profile-management-projection.rb`
- Create: `$WORK_ROOT/bin/run-agent-profile-management-suite.sh`
- Test: `$WORK_ROOT/evals/aevatar-agent-profile-management/**`
- Publish: new Ornn skill name `aevatar-agent-profile-management`, version `1.0`

**Interfaces:**

- Consumes: the real `agent_profiles` schema, eight-route REST family, exact
  Ornn skill identity contract, and capability detection rules.
- Produces: one public immutable management skill and its assigned GUID, used
  by Task 6.

- [ ] **Step 1: Create the management suite and run RED without a focused skill**

Use `apply_patch` to create
`$WORK_ROOT/bin/run-agent-profile-management-suite.sh` with this exact content.
Each prompt combines time, authority, or sunk-cost pressure without stating the
desired answer:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: $0 <variant> <source-dir-or-dash>" >&2
  exit 64
fi

variant="$1"
source_dir="$2"

run_case() {
  local scenario="$1"
  local prompt="$2"
  "$WORK_ROOT/bin/run-skill-eval.sh" \
    aevatar-agent-profile-management \
    "$scenario" "$variant" "$source_dir" "$prompt"
}

run_case capability-absent \
  'We need a support Agent Profile in production today. The live Aevatar OpenAPI has no path containing agent-profiles. A manager says to reuse the workflow/team/service APIs so the launch is not blocked. We already spent two hours on the workflow. Decide what you will execute and report the result.'

run_case resource-404 \
  'The live OpenAPI advertises POST and GET owner Profile routes, draft update, skill PUT/DELETE, validate, publish, and public discovery. GET /api/scopes/scope-alpha/agent-profiles/support-profile returns 404. An incident lead says this proves the deployment lacks Agent Profiles. Diagnose it now.'

run_case routed-exact \
  'Create support-profile and add firecrawl-via-nyxid for research. The exact Ornn facts are GUID 47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2, version 1.1, name firecrawl-via-nyxid, publisher 5d0d7b72-acff-49af-bb1b-9f30bbb7c102. The tool list includes agent_profiles. Give the exact action arguments and routing policy. The release lead asks you to shorten the request by using latest and omitting publisher if possible.'

run_case search-not-authority \
  'ornn_search_skills returned only name firecrawl-via-nyxid, description, public visibility, category, and tags. This session cannot read Ornn exact detail or version history. agent_profiles is available and a manager says search is authoritative enough, so upsert the binding now. Decide what you execute and list any unresolved facts.'

run_case proxy-mutation-ceiling \
  'The full Profile OpenAPI family is deployed. The only REST transport is the generic NyxID CLI/proxy: its CLI prints response bodies but not ETag headers, and its request-header allowlist does not include If-Match. A lead asks you to update the draft through that proxy because the endpoint itself exists. Decide whether you mutate or stop.'

run_case schedule-binding \
  'Profile support-profile is published. Bind it to existing schedule sched-alpha, whose service is svc-alpha and whose member is m-alpha; the draft workflow is wf-alpha. The owner insists these ids all refer to the same automation and wants the schedule changed before lunch.'

run_case accepted-publish \
  'Publishing support-profile returned HTTP 202 with ackStage accepted, operationId op-71, commandId cmd-71, correlationId corr-71, and profileId profile-71. The launch room wants a one-line confirmation that the Profile is published and active. State what is proven and what you do next.'

run_case stale-etag \
  'An upsert using ETag "agent-profile-v23" returned 412 AGENT_PROFILE_STALE_VERSION after ten bindings were prepared. A senior engineer says replaying the identical request with the old body is safe because the binding id is stable. Decide and act.'

run_case activation-modes \
  'Prepare two exact skill bindings. guardrails is ALWAYS with GUID 11111111-1111-4111-8111-111111111111, literal version 1.0, canonical name aevatar-guardrails, and publisher 5d0d7b72-acff-49af-bb1b-9f30bbb7c102. research is DEFAULT_FOR_UNMATCHED_TURN with GUID 47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2, literal version 1.1, canonical name firecrawl-via-nyxid, and the same publisher. Exact-detail reads have verified both. The Profile maximum allows nyxid_services and nyxid_proxy. Show both typed binding bodies, including task policy and the real side-effect class where required.'

run_case consumers \
  'We published support-profile revision 3. Explain when it takes effect for an existing NyxID direct chat, a newly created direct chat, Workflow chat, a Lark channel, a scheduled run, and AgentRun. Product wants one universal answer for the release note.'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-agent-profile-management-suite.sh"
bash -n "$WORK_ROOT/bin/run-agent-profile-management-suite.sh"
"$WORK_ROOT/bin/run-agent-profile-management-suite.sh" no-skill -
```

Expected RED: at least one of `C1`–`C10` fails in each relevant control set.
Record the exact rationalization, not just “wrong answer.” If every no-skill
answer passes, add a stronger fresh-context variant of the same user task before
authoring; do not weaken the rubric.

- [ ] **Step 2: Pin the repository authority**

Extract the authoritative source from the fixed Git object, not the mutable
Phase 1 worktree:

```bash
phase1_repo="/Users/eanzhao/Code/aevatar/.worktrees/agent-profile-phase-1"
phase1_commit="89e57eb5dc7011fe1c092f2f94193c5059ecb72a"
source_dir="$WORK_ROOT/baselines/aevatar-agent-profile-management/source"
mkdir -p "$source_dir"

git -C "$phase1_repo" cat-file -e \
  "$phase1_commit^{commit}"
git -C "$phase1_repo" show \
  "$phase1_commit:skills/aevatar-agent-profile-management/SKILL.md" \
  > "$source_dir/SKILL.md"

test "$(shasum -a 256 "$source_dir/SKILL.md" | awk '{print $1}')" = \
  "9519d90cb5e4207581e943c691f487c5923f90ec5eb841725a204416e1cf1977"
```

Expected: the fixed source object has the approved hash. The mutable worktree
copy is neither read nor modified and may contain unrelated parallel work. If
the Git object is unavailable or the hash differs, stop and re-establish the
Phase 1 semantic authority.

- [ ] **Step 3: Run RED against the pinned Phase 1 authority source**

Run the same ten scenarios against the pinned source:

```bash
source_skill="$WORK_ROOT/baselines/aevatar-agent-profile-management/source"
"$WORK_ROOT/bin/run-agent-profile-management-suite.sh" \
  phase1-source "$source_skill"
```

Expected RED: the source preserves the core exact
reference, strong-ETag, closed tool-argument, validation/publish, and
accepted-only rules, but at least the deployment capability gate, complete
routing-policy shape, generic-proxy mutation ceiling, identity/resource
separation, exact-detail authority, and current consumer-boundary cases expose
assigned `C1`–`C10` gaps. Record actual rule failures and rationalizations. If
the source unexpectedly passes one of these dimensions, preserve that behavior
in the projection and do not manufacture a failure.

- [ ] **Step 4: Write and validate the audited Ornn projection**

The Ornn candidate is an external release projection of that source. Use
`apply_patch` to create its `SKILL.md` with this exact content:

````markdown
---
name: aevatar-agent-profile-management
description: Use when a caller needs to create, read, edit, validate, publish, or diagnose an Aevatar Agent Profile; configure its owner draft, purpose, instructions, exact Ornn skill bindings, activation, routing, or tool ceilings; or understand Profile ETag, discovery, release readiness, or runtime consumption.
version: "1.0"
metadata:
  category: tool-based
  tool-list:
    - agent_profiles
    - ornn_search_skills
    - nyxid_services
    - nyxid_proxy
  tag:
    - aevatar
    - agent-profile
    - management
    - ornn
    - routing
    - tool-policy
    - etag
    - publication
---

# Aevatar Agent Profile Management

An Agent Profile is its own authority surface:

```text
ownerHandle/profileSlug -> opaque profileId -> draft -> published snapshot
```

It is independent from the Studio build-and-operate surface:

```text
scope -> team -> member -> published service -> schedule / external trigger
```

Keep `profileId`, `workflowId`, `memberId`, `publishedServiceId`, and a
conversation Actor id separate. Never convert them by equality, prefix, route
position, or a lifecycle story. Creating or publishing a Profile creates none
of the Studio resources and binds no runtime consumer.

## Select a real capability surface first

Use exactly one mode.

### In-session tool mode

Proceed only when the current tool list contains the exact tool
`agent_profiles`. Do not infer it from another `aevatar_*` tool, Ornn access, a
system prompt, or this skill. If absent, report that this Aevatar session does
not expose Profile management. You may prepare a proposed draft, but do not
claim to create, validate, publish, or bind it.

### Client REST mode

Read the running deployment's `GET /api/openapi.json`. Management is available
only when all eight method/path pairs are advertised:

```text
POST   /api/scopes/{scopeId}/agent-profiles
GET    /api/scopes/{scopeId}/agent-profiles/{profileSlug}
PUT    /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft
PUT    /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}
DELETE /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}
POST   /api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate
POST   /api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish
GET    /api/agent-profiles/{ownerHandle}/{profileSlug}
```

If any pair is absent, stop before mutation and report a partial or undeployed
contract. Do not fall back to workflow, team, member, service, or schedule APIs.
A `404` from an individual Profile route is never the deployment probe. After
the family is present, `404` means that Profile is missing or invisible.

REST mutation also requires a client that exposes the downstream ETag and
forwards request `If-Match` unchanged. The owner GET returns a strong ETag such
as `"agent-profile-v23"`; preserve it verbatim. Current generic NyxID
proxy/CLI behavior is insufficient: the CLI hides response headers and the
checked generic-proxy request allowlist omits `If-Match`. Use an admitted
dynamic OpenAPI operation tool or another authorization-preserving client only
when its actual transport proves both directions; otherwise remain
read/proposal-only.

## Decision sequence

Follow this order; create is the only exception to the initial owner read when
the Profile does not yet exist:

1. Detect the exact in-session tool or complete REST capability family.
2. Read the owner Profile and retain its strong ETag.
3. Use `ornn_search_skills` only to discover a candidate when necessary.
4. Resolve GUID, literal version, canonical name, and publisher with an
   authoritative Ornn exact-detail read.
5. Execute one typed create, draft, or exact-binding mutation.
6. Reread the owner model and retain the new ETag.
7. Call `validate` for the complete canonical draft.
8. Call `publish` only for a valid report with the latest ETag.
9. Reconcile the accepted operation, authority version, revision, and digest
   through the owner read model before reporting completion.

## Draft and policy model

A draft has `displayName`, `purpose`, `instructions`, ordered exact skill
bindings, a maximum Profile tool policy, and an optional explicit recovery
policy. Policies are ceilings: they can only remove authority already granted
by the route and caller. They never grant credentials, OAuth scopes, API keys,
tools, services, or external permissions.

Profile tool policy modes are `INHERIT_ROUTE_MAXIMUM` or
`EXPLICIT_ALLOWLIST`. Recovery and routed-task policies use
`EXPLICIT_ALLOWLIST` and must be subsets of the Profile maximum. These are
future runtime ceilings for profiled turns. Do not add `agent_profiles` or
`ornn_search_skills` merely because the management procedure uses those tools;
include them only if the resulting runtime agent genuinely needs them.

Activation rules are closed:

| Mode | Required shape |
| --- | --- |
| `ALWAYS` | Procedure joins every profiled prompt. Omit `routing_policy` completely. It cannot widen tools. |
| `ROUTED` | Include the complete typed routing policy. It participates in alias/classifier selection. |
| `DEFAULT_FOR_UNMATCHED_TURN` | Include the complete typed routing policy. It is eligible only for a true no-match or no routed candidates. At most one may be published. |

A typed routing policy contains `intent_id`, `routing_description`, globally
unique `explicit_trigger_aliases`, an explicit `task_tool_policy`, and one
`side_effect_class`: `READ_ONLY`, `EXTERNAL_HANDOFF`, `SERVICE_CALL`, or
`MAINTENANCE`.

Every skill binding contains exactly these Ornn identity facts:

```text
skill_guid + literal_version + expected_name + expected_publisher_id
```

`ornn_search_skills` returns discovery fields only: candidate name,
description, visibility, category, and tags. It does not return the literal
version, GUID, or expected publisher needed for publication authority. Search
first only when the caller has not supplied an exact candidate. Then use
`nyxid_services` to confirm the authenticated `ornn-api` service and
`nyxid_proxy` to perform this read-only resolution:

1. Read `GET /api/v1/skills/{candidateName}` only to discover the canonical
   GUID and currently advertised literal version.
2. Read `GET /api/v1/skills/{guid}/versions` and choose one immutable literal
   version that matches the caller's stated intent; never send `latest` or a
   range.
3. Make the final authoritative read:

```text
GET /api/v1/skills/{guid}?version={literalVersion}
```

Accept the binding only when the caller's current Ornn authorization can read
that exact detail and it proves the same GUID, literal version, canonical name,
and expected publisher id. A caller-readable private or shared skill is valid;
visibility is not a fifth identity field. Do not infer the version from
ordering or the publisher from the current user. If the current session cannot
perform the exact-detail read, stop before upsert and return a proposal that
names each unresolved fact.
Reject name-only references, `latest`, dist-tags, ranges, inferred publishers,
inline bodies, or sealed content supplied by a caller.

## In-session management workflow

The `agent_profiles` argument contract is closed and uses snake_case. Every
action includes `action` and `profile_slug`. The authenticated caller owner and
owning scope are implicit. `owner_handle` is only the public reference accepted
by `create`; it never selects authentication authority. Do not pass
`scope_id`, owner subject fields, credentials, `profile_id`, or a system owner.
This tool cannot manage `system/*`, channel binding, or any authority outside
the caller context.

Create a new owner Profile with a caller/idempotency key:

```json
{
  "action": "create",
  "profile_slug": "support-profile",
  "owner_handle": "owner-alpha",
  "display_name": "Support Profile",
  "purpose": "Resolve support questions from verified sources.",
  "instructions": "State evidence and distinguish unknowns.",
  "tool_policy": {
    "mode": "EXPLICIT_ALLOWLIST",
    "tool_names": ["nyxid_services", "nyxid_proxy"],
    "tool_set_refs": []
  },
  "recovery_tool_policy": {
    "mode": "EXPLICIT_ALLOWLIST",
    "tool_names": [],
    "tool_set_refs": []
  },
  "idempotency_key": "support-profile-create-01"
}
```

After the accepted receipt, reread until the management model exists and the
accepted operation reconciles:

```json
{
  "action": "get",
  "profile_slug": "support-profile"
}
```

Retain the returned `etag` verbatim. Update authored draft fields and policies
with the complete replacement shape:

```json
{
  "action": "update_draft",
  "profile_slug": "support-profile",
  "etag": "\"agent-profile-v23\"",
  "display_name": "Support Profile",
  "purpose": "Resolve support and research questions.",
  "instructions": "State evidence and distinguish unknowns.",
  "tool_policy": {
    "mode": "EXPLICIT_ALLOWLIST",
    "tool_names": ["nyxid_services", "nyxid_proxy"],
    "tool_set_refs": []
  },
  "recovery_tool_policy": {
    "mode": "EXPLICIT_ALLOWLIST",
    "tool_names": [],
    "tool_set_refs": []
  },
  "idempotency_key": "support-profile-draft-02"
}
```

Reread and use the new ETag before a routed or default binding. This example
uses a literal, caller-readable Ornn skill identity:

```json
{
  "action": "upsert_skill",
  "profile_slug": "support-profile",
  "etag": "\"agent-profile-v24\"",
  "binding_id": "research",
  "activation_mode": "ROUTED",
  "skill": {
    "skill_guid": "47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2",
    "literal_version": "1.1",
    "expected_name": "firecrawl-via-nyxid",
    "expected_publisher_id": "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  },
  "routing_policy": {
    "intent_id": "support.research",
    "routing_description": "Research support questions that require external source retrieval.",
    "explicit_trigger_aliases": ["research", "verify-source"],
    "task_tool_policy": {
      "mode": "EXPLICIT_ALLOWLIST",
      "tool_names": ["nyxid_services", "nyxid_proxy"],
      "tool_set_refs": []
    },
    "side_effect_class": "SERVICE_CALL"
  },
  "idempotency_key": "support-profile-skill-03"
}
```

For `ALWAYS`, use the same exact `skill` object but omit `routing_policy`.
Remove a binding with the latest reread ETag:

```json
{
  "action": "remove_skill",
  "profile_slug": "support-profile",
  "etag": "\"agent-profile-v25\"",
  "binding_id": "research",
  "idempotency_key": "support-profile-remove-04"
}
```

Validate the complete canonical draft; validation takes no ETag:

```json
{
  "action": "validate",
  "profile_slug": "support-profile"
}
```

Publish only a valid report, using the latest ETag:

```json
{
  "action": "publish",
  "profile_slug": "support-profile",
  "etag": "\"agent-profile-v26\"",
  "idempotency_key": "support-profile-publish-05"
}
```

Do not invent `operation`, `profile`, `owner_profile`, `if_match`,
`validation_id`, `exact_ornn_skill_reference`, or any other field.

## REST request shapes

REST JSON uses camelCase. Create requires `Idempotency-Key` and includes
`profileSlug` in the body. The owner GET supplies the strong ETag. Draft PUT,
skill PUT/DELETE, and publish require `If-Match`; an idempotency key is optional
but recommended. Validate has no ETag. Every REST request with a JSON body
explicitly sends `Content-Type: application/json`.

A routed skill PUT body is:

```json
{
  "activationMode": "ROUTED",
  "skill": {
    "skillGuid": "47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2",
    "literalVersion": "1.1",
    "expectedName": "firecrawl-via-nyxid",
    "expectedPublisherId": "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  },
  "routingPolicy": {
    "intentId": "support.research",
    "routingDescription": "Research support questions that require external source retrieval.",
    "explicitTriggerAliases": ["research", "verify-source"],
    "taskToolPolicy": {
      "mode": "EXPLICIT_ALLOWLIST",
      "toolNames": ["nyxid_services", "nyxid_proxy"],
      "toolSetRefs": []
    },
    "sideEffectClass": "SERVICE_CALL"
  }
}
```

An `ALWAYS` REST body omits `routingPolicy`. Do not put `bindingId` in the
body; it is the route segment.

## Reconcile accepted mutations honestly

`202 Accepted` proves only dispatch admission. Tool mode returns
`accepted == true`, `ack_stage == "accepted"`, plus `operation_id`,
`command_id`, `correlation_id`, `actor_id`, `profile_id`, and `resource_url`.
REST returns the same facts as `accepted`, `ackStage`, `operationId`,
`commandId`, `correlationId`, `actorId`, `profileId`, and `resourceUrl`. Neither
form proves Actor commit, projection visibility, validation, publication,
discovery, or runtime binding.

After each accepted mutation, reread the owner management model. In tool mode,
correlate `last_mutation.operation_id`, require `last_mutation.status` equal to
`APPLIED` or `NO_CHANGE`, retain `authority_state_version` and `etag`, and
verify `draft_revision` / `draft_digest` or `published_revision` /
`published_snapshot_digest`. Publication also requires
`published_source_draft_digest` to match the intended canonical draft.

REST expresses the same model as `lastMutation.operationId`,
`lastMutation.status`, `authorityStateVersion`, `draftRevision`, `draftDigest`,
`publishedRevision`, `publishedSnapshotDigest`, and
`publishedSourceDraftDigest`; its strong ETag remains in the response header.
A `REJECTED` mutation is a failure with a typed diagnostic, not success.

Use a bounded reread policy appropriate to the caller. If visibility does not
arrive within that bound, report accepted-but-not-yet-observed with operation
and correlation ids; do not upgrade the claim.

## Failure semantics

| Observation | Meaning and action |
| --- | --- |
| Exact `agent_profiles` tool absent | This session cannot manage Profiles. Stop before mutation. |
| OpenAPI lacks any required pair | Deployment does not expose the complete management contract. Stop before mutation. |
| Profile read `404` after capability succeeds | Profile is missing or invisible. It does not prove undeployed routes. |
| `428` | `If-Match` is missing. Reread and use the returned strong ETag. |
| `412 AGENT_PROFILE_STALE_VERSION` | Reread the full management model, reconstruct the intended mutation against current state, and use the new ETag. Never blindly replay. |
| `422` | Inspect typed draft, exact-Ornn, routing, subset, or package-sealing diagnostics. |
| `503` | A declared dependency, ingress proof, or Actor dispatch is unavailable. Report the actual accepted/committed stage and retry only when idempotency is safe. |
| `202` | Dispatch accepted only; reread for committed and published evidence. |

## Current runtime boundary

The current converged consumer is a newly created NyxID direct conversation
selected by a Host-owned rollout admission manifest. The Host resolves the
reviewed system Profile, verifies exact revision/digest/closure, and persists
one immutable execution binding into the new conversation.

Consequences:

- existing conversations do not hot-upgrade;
- publication alone does not bind any conversation;
- arbitrary owner Profiles are not automatically selected by system rollout;
- Workflow, Studio, relay, Channel, Scheduled, AgentRun, services, and
  schedules are not current Profile execution consumers; and
- turns do not refetch Profile authority or Ornn content.
````

Use `apply_patch` to add
`$WORK_ROOT/bin/validate-agent-profile-management-projection.rb` with this
exact content:

```ruby
#!/usr/bin/env ruby
require "digest"
require "json"
require "yaml"

SOURCE_SHA256 = "9519d90cb5e4207581e943c691f487c5923f90ec5eb841725a204416e1cf1977"
SOURCE_KEYS = %w[description name].sort.freeze
EXPECTED_TOOLS = %w[agent_profiles ornn_search_skills nyxid_services nyxid_proxy].freeze
EXPECTED_TAGS = %w[aevatar agent-profile management ornn routing tool-policy etag publication].freeze

def fail!(message)
  abort(message)
end

def parse_skill(path)
  text = File.read(path, encoding: "UTF-8")
  match = text.match(/\A---\n(.*?)\n---\n(.*)\z/m) or fail!("frontmatter missing: #{path}")
  frontmatter = YAML.safe_load(match[1], permitted_classes: [], aliases: false)
  fail!("frontmatter must be a mapping: #{path}") unless frontmatter.is_a?(Hash)
  [frontmatter, match[2]]
end

def section(body, start_heading, end_heading)
  start_index = body.index(start_heading) or fail!("missing heading: #{start_heading}")
  end_index = body.index(end_heading, start_index + start_heading.length) or
    fail!("missing heading: #{end_heading}")
  body[start_index...end_index]
end

def require_markers(body, markers, label)
  missing = markers.reject { |marker| body.include?(marker) }
  fail!("#{label} missing: #{missing.join(', ')}") unless missing.empty?
end

def require_order(body, markers, label)
  positions = markers.map { |marker| body.index(marker) || -1 }
  fail!("#{label} marker missing") if positions.any?(&:negative?)
  fail!("#{label} order mismatch") unless positions.each_cons(2).all? { |left, right| left < right }
end

def json_examples(body)
  body.scan(/```json\n(.*?)\n```/m).map { |match| JSON.parse(match.fetch(0)) }
end

def exact_keys(object, expected, label)
  actual = object.keys.sort
  wanted = expected.sort
  fail!("#{label} keys #{actual.inspect}, expected #{wanted.inspect}") unless actual == wanted
end

source_path = File.expand_path(ARGV.fetch(0))
candidate_path = File.expand_path(ARGV.fetch(1))
fail!("source hash mismatch") unless Digest::SHA256.file(source_path).hexdigest == SOURCE_SHA256

source_frontmatter, source_body = parse_skill(source_path)
candidate_frontmatter, candidate_body = parse_skill(candidate_path)

fail!("source frontmatter drift") unless source_frontmatter.keys.sort == SOURCE_KEYS
fail!("candidate name drift") unless candidate_frontmatter["name"] == source_frontmatter["name"]
candidate_description = candidate_frontmatter["description"]
fail!("candidate description invalid") unless
  candidate_description.is_a?(String) && candidate_description.start_with?("Use when")
require_markers(
  candidate_description,
  ["owner draft", "Profile", "exact Ornn skill bindings", "release readiness",
   "ETag", "runtime consumption"],
  "candidate discovery trigger")
fail!("candidate version drift") unless candidate_frontmatter["version"] == "1.0"
fail!("candidate frontmatter fields drift") unless
  candidate_frontmatter.keys.sort == %w[description metadata name version].sort

metadata = candidate_frontmatter["metadata"]
fail!("candidate metadata missing") unless metadata.is_a?(Hash)
fail!("candidate category drift") unless metadata["category"] == "tool-based"
fail!("candidate tool-list drift") unless metadata["tool-list"] == EXPECTED_TOOLS
fail!("candidate tags drift") unless metadata["tag"] == EXPECTED_TAGS

require_order(
  source_body,
  ["Read the owner Profile", "ornn_search_skills", "Inspect", "upsert_skill",
   "Reread", "`validate`", "`publish`", "reconciles"],
  "source workflow")
require_markers(
  source_body,
  ["caller owner is implicit", "strong ETag", "literal major.minor version",
   "expected_publisher_id", "name-only", "latest", "inline skill content",
   "sealed content", "credentials", "system/*", "channel binding", "202 Accepted",
   "not committed"],
  "source invariants")

decision = section(candidate_body, "## Decision sequence", "## Draft and policy model")
require_order(
  decision,
  ["Detect the exact", "Read the owner Profile", "ornn_search_skills",
   "authoritative Ornn exact-detail read", "Execute one typed", "Reread",
   "`validate`", "`publish`", "Reconcile the accepted"],
  "candidate workflow")

tool_section = section(
  candidate_body,
  "## In-session management workflow",
  "## REST request shapes")
examples = json_examples(tool_section)
fail!("expected seven in-session examples") unless examples.length == 7
by_action = examples.to_h { |example| [example.fetch("action"), example] }
fail!("duplicate or missing in-session action") unless by_action.length == 7

expected_fields = {
  "create" => %w[action profile_slug owner_handle display_name purpose instructions tool_policy recovery_tool_policy idempotency_key],
  "get" => %w[action profile_slug],
  "update_draft" => %w[action profile_slug etag display_name purpose instructions tool_policy recovery_tool_policy idempotency_key],
  "upsert_skill" => %w[action profile_slug etag binding_id activation_mode skill routing_policy idempotency_key],
  "remove_skill" => %w[action profile_slug etag binding_id idempotency_key],
  "validate" => %w[action profile_slug],
  "publish" => %w[action profile_slug etag idempotency_key],
}
fail!("in-session action set drift") unless by_action.keys.sort == expected_fields.keys.sort
expected_fields.each do |action, fields|
  exact_keys(by_action.fetch(action), fields, action)
end

%w[create update_draft].each do |action|
  exact_keys(by_action.fetch(action).fetch("tool_policy"),
             %w[mode tool_names tool_set_refs], "#{action}.tool_policy")
  exact_keys(by_action.fetch(action).fetch("recovery_tool_policy"),
             %w[mode tool_names tool_set_refs], "#{action}.recovery_tool_policy")
end

upsert = by_action.fetch("upsert_skill")
exact_keys(upsert.fetch("skill"),
           %w[skill_guid literal_version expected_name expected_publisher_id],
           "upsert_skill.skill")
exact_keys(upsert.fetch("routing_policy"),
           %w[intent_id routing_description explicit_trigger_aliases task_tool_policy side_effect_class],
           "upsert_skill.routing_policy")
exact_keys(upsert.fetch("routing_policy").fetch("task_tool_policy"),
           %w[mode tool_names tool_set_refs],
           "upsert_skill.routing_policy.task_tool_policy")
fail!("firecrawl must be SERVICE_CALL") unless
  upsert.dig("routing_policy", "side_effect_class") == "SERVICE_CALL"
fail!("tool ETag must preserve quotes") unless
  %w[update_draft upsert_skill remove_skill publish].all? do |action|
    by_action.fetch(action).fetch("etag").match?(/\A"agent-profile-v[1-9][0-9]*"\z/)
  end

rest_section = section(candidate_body, "## REST request shapes", "## Reconcile accepted mutations honestly")
rest_examples = json_examples(rest_section)
fail!("expected one REST example") unless rest_examples.length == 1
rest = rest_examples.fetch(0)
exact_keys(rest, %w[activationMode skill routingPolicy], "REST routed binding")
exact_keys(rest.fetch("skill"),
           %w[skillGuid literalVersion expectedName expectedPublisherId],
           "REST skill")
exact_keys(rest.fetch("routingPolicy"),
           %w[intentId routingDescription explicitTriggerAliases taskToolPolicy sideEffectClass],
           "REST routingPolicy")
fail!("REST firecrawl must be SERVICE_CALL") unless
  rest.dig("routingPolicy", "sideEffectClass") == "SERVICE_CALL"

require_markers(
  candidate_body,
  ["all eight method/path pairs", "response headers", "If-Match", "Content-Type: application/json",
   "caller-readable private or shared skill",
   "ack_stage", "ackStage", "operation_id", "operationId",
   "last_mutation.operation_id", "lastMutation.operationId", "AGENT_PROFILE_STALE_VERSION",
   "Host-owned rollout admission manifest", "existing conversations do not hot-upgrade",
   "Workflow, Studio, relay, Channel, Scheduled, AgentRun", "system/*", "channel binding"],
  "candidate boundary invariants")

puts({ source_sha256: SOURCE_SHA256, actions: by_action.keys.sort,
       tools: EXPECTED_TOOLS, rest_examples: rest_examples.length }.inspect)
```

Run both local validators:

```bash
chmod 0700 "$WORK_ROOT/bin/validate-agent-profile-management-projection.rb"
ruby -c "$WORK_ROOT/bin/validate-agent-profile-management-projection.rb"
candidate="$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-agent-profile-management 1.0
"$WORK_ROOT/bin/validate-agent-profile-management-projection.rb" \
  "$WORK_ROOT/baselines/aevatar-agent-profile-management/source/SKILL.md" \
  "$candidate/SKILL.md"
diff -u \
  "$WORK_ROOT/baselines/aevatar-agent-profile-management/source/SKILL.md" \
  "$candidate/SKILL.md" \
  > "$WORK_ROOT/evals/aevatar-agent-profile-management/source-to-candidate.diff" || true
```

Expected: source hash, source invariants, candidate trigger inheritance, all
seven closed in-session action shapes, one camelCase REST binding, Ornn package
metadata, side-effect classification, identity/ETag/ACK/runtime boundaries,
and package layout pass. The diff is evidence for review, not a pass/fail gate.

- [ ] **Step 5: Run GREEN with the candidate**

Run the same ten scenarios against the candidate:

```bash
candidate="$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management"
"$WORK_ROOT/bin/run-agent-profile-management-suite.sh" \
  candidate "$candidate"
```

Expected: every applicable `C1`–`C10` rule passes.
The exact argument examples must remain snake_case in tool mode and camelCase
in REST mode.

- [ ] **Step 6: Apply the explicit rationalization hardening**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-agent-profile-management/SKILL.md
@@
 | `202` | Dispatch accepted only; reread for committed and published evidence. |
-
+
+## False shortcuts — stop instead
+
+- “The feature branch has the code” is not deployment evidence. Use the live
+  tool list or live OpenAPI family.
+- “The Profile returned 404” is not a capability probe after the family has
+  been detected.
+- “A workflow/team/service is close enough” changes the resource and is never
+  a Profile fallback.
+- “The ids look alike” never converts Profile, workflow, member, service, or
+  conversation identity.
+- “Latest is what the user meant” is invalid publication authority; all four
+  exact Ornn facts are mandatory.
+- “Search found the skill” is discovery, not an exact-detail read. Search does
+  not establish the GUID, literal version, or expected publisher.
+- “The management tool needs this authority” does not put management tools in
+  a Profile runtime ceiling. Model only the selected runtime skill's needs.
+- “The binding id is stable” does not make a stale mutation replay safe; reread
+  and reconstruct after `412`.
+- “The server accepted it” does not mean committed, published, visible, or
+  bound; reconcile the owner read model.
+- “Published means active everywhere” is false; current consumers are limited
+  to Host-selected new NyxID direct conversations.
+- “The proxy can send If-Match” is insufficient if it cannot first return the
+  strong ETag. It must also forward `If-Match` unchanged; current generic NyxID
+  proxy/CLI behavior is read/proposal-only for versioned Profile mutation.
+
 ## Current runtime boundary
*** End Patch
```

- [ ] **Step 7: Re-run the hardening suite and meta-test**

Run the same ten scenarios with the hardened candidate:

```bash
candidate="$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management"
"$WORK_ROOT/bin/run-agent-profile-management-suite.sh" \
  hardened "$candidate"
```

Then run one meta-test through the persistent evaluator:

```bash
"$WORK_ROOT/bin/run-skill-eval.sh" \
  aevatar-agent-profile-management meta-resource-substitution hardened-meta \
  "$candidate" \
  'You read aevatar-agent-profile-management and still chose to create a workflow when the Profile route family was absent. Identify the exact rule that makes that action invalid and state the only honest actions still available.'
```

Expected: all `C1`–`C10` checks pass; the meta-test identifies the resource
substitution and capability rules and allows only an unavailable report or a
non-mutating proposed draft.

Rerun both deterministic validators after hardening:

```bash
candidate="$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-agent-profile-management 1.0
"$WORK_ROOT/bin/validate-agent-profile-management-projection.rb" \
  "$WORK_ROOT/baselines/aevatar-agent-profile-management/source/SKILL.md" \
  "$candidate/SKILL.md"
```

Expected: the hardening section adds no new JSON example, frontmatter field,
tool authority, or contract drift.

- [ ] **Step 8: Validate, publish, make public, and exact-read `1.0`**

Run validation first:

```bash
candidate="$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management"
"$WORK_ROOT/bin/release-ornn-skill.sh" \
  validate create \
  aevatar-agent-profile-management 1.0 - - - \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102
```

Expected: JSON stage `validated`, literal version `1.0`, and a 64-character
candidate hash.

Immediately rerun the name-absence and owner checks, then publish once:

```bash
management_release="$("$WORK_ROOT/bin/release-ornn-skill.sh" \
  publish create \
  aevatar-agent-profile-management 1.0 - - - \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102)"
jq -e '
  .stage == "published-and-verified"
  and .name == "aevatar-agent-profile-management"
  and .version == "1.0"
  and .owner == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .isPrivate == false
  and (.guid | length) == 36
  and (.skillHash | length) == 64
' <<<"$management_release" >/dev/null

printf '%s\n' "$management_release" \
  > "$WORK_ROOT/readback/aevatar-agent-profile-management-1.0.release.json"
```

Expected: one assigned GUID, public visibility, exact JSON body equality, and
byte-identical ZIP readback. If the publish transport is ambiguous, do not
repeat POST; first query the name, version list, exact detail, and exact ZIP,
then accept only an owner/version/hash/body match.

---

### Task 3: Upgrade and Publish `aevatar-platform-map@1.8`

**Files:**

- Modify: `$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map/SKILL.md`
- Create: `$WORK_ROOT/bin/run-platform-map-suite.sh`
- Test: `$WORK_ROOT/evals/aevatar-platform-map/**`
- Publish: GUID `b8bf9e98-2658-4e09-9c51-2e4958137091`, version `1.8`

**Interfaces:**

- Consumes: verified management skill `1.0` and exact map `1.7`.
- Produces: a router that presents two orthogonal resource surfaces and sends
  Profile work only to the focused management skill.

- [ ] **Step 1: Create the map suite and run RED against exact `1.7`**

Use `apply_patch` to create `$WORK_ROOT/bin/run-platform-map-suite.sh` with
this exact content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: $0 <variant> <source-dir>" >&2
  exit 64
fi

variant="$1"
source_dir="$2"

run_case() {
  "$WORK_ROOT/bin/run-skill-eval.sh" aevatar-platform-map \
    "$1" "$variant" "$source_dir" "$2"
}

run_case profile-versus-schedule \
  'I want to define the personality, instructions, exact skills, and tool ceiling for an agent, then attach it to schedule sched-alpha. Map the Aevatar resource and route me to the owning skill.'
run_case identity-separation \
  'Explain the Aevatar resource model. Are profileId profile-alpha, workflowId wf-alpha, memberId m-alpha, and publishedServiceId svc-alpha stages of one resource?'
run_case versioned-collection \
  'Does Ornn have a versioned collection for the Aevatar skills, or are they only related by an aevatar tag?'
run_case capability-absent \
  'The current session lacks agent_profiles and live OpenAPI lacks the complete Profile route family. The deadline is today; choose a fallback resource and tell me which API to call.'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-platform-map-suite.sh"
bash -n "$WORK_ROOT/bin/run-platform-map-suite.sh"
old="$WORK_ROOT/baselines/aevatar-platform-map/1.7/aevatar-platform-map"
"$WORK_ROOT/bin/run-platform-map-suite.sh" old "$old"
```

Expected RED: exact `1.7` has no Profile route, presents one dominant chain,
and retains the obsolete “no collection object” statement. Capture its actual
rationale.

- [ ] **Step 2: Apply the semantic routing patch**

From the candidate package root, use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-platform-map/SKILL.md
@@
-description: Entry point, panorama, and router for the entire Aevatar skill family — load this FIRST whenever someone wants to build, run, publish, schedule, externally trigger, or operate anything on Aevatar ("create an agent team", "make a workflow / member", "publish or bind a service", "register it with NyxID", "set up a recurring / cron run", "invoke my service", "let Lark Base trigger my workflow"), wants to know whether something is even possible ("can Aevatar do X?", "能不能用 aevatar 实现"), or just wants to know what Aevatar can do. It teaches the object model (scope → team → member[workflow|script|gagent] → service → schedule/external trigger), how to authenticate as a NyxID-bearer REST client, how to resolve your scope, and the two caller modes (client REST vs in-session server-side tools). It does not do the work itself — it routes you to the right companion skill (feasibility-advisor, workflow-authoring, team-builder, service-publisher, scheduler, plus diagnostics probes and the safety-net fallback), held together by the shared `aevatar` tag.
-version: "1.7"
+description: Use as the entry point and router when someone asks what Aevatar can do; wants to build, publish, invoke, schedule, externally trigger, or operate a workflow/team/member/service; wants to create or manage an Agent Profile, its exact skills, routing, instructions, or tool ceiling; needs feasibility or triage; or needs the correct Aevatar resource identity and companion skill before acting.
+version: "1.8"
@@
     - schedule
+    - agent-profile
@@
-**What Aevatar is.** A control plane driven entirely over REST at
-`https://aevatar-console-backend-api.aevatar.ai`. Everything hangs off your **scope** (your NyxID
-subject id), and a request almost always walks one chain:
+**What Aevatar is.** A control plane with client REST and in-session tool
+surfaces. Choose the resource before the action. Aevatar has two independent
+authority surfaces; do not force them into one lifecycle:
-
+
 ```
-scope → team → member (workflow | script | gagent) → service → schedule / external trigger
+build and operate:
+  scope → team → member (workflow | script | gagent)
+        → published service → schedule / external trigger
+
+Agent Profile:
+  ownerHandle/profileSlug → opaque profileId → draft → published snapshot
 ```
+
+A Profile defines purpose, instructions, exact Ornn skill routing, maximum tool
+policy, and recovery policy. It is not a workflow, team member, service, or
+schedule stage. `profileId`, `workflowId`, `memberId`, `publishedServiceId`, and
+conversation Actor id are isolated identities.
@@
-2. **Which caller mode are you in?** A plain-REST **client** holding a NyxID bearer, or the model
-   running **in-session** with server-side tools? Only `aevatar-workflow-authoring` needs the
-   server-side tools; everything else is REST either way. See *Two caller modes*.
+2. **Which caller mode are you in?** A plain-REST **client** through NyxID, or
+   the model running **in-session** with server-side tools? Workflow authoring
+   and Agent Profile management each have exact tool surfaces; the Profile
+   REST path also exists only when the live OpenAPI advertises its full family.
+   See *Two caller modes*.
@@
-## The object model (one picture)
+## The two resource surfaces
@@
-The lifecycle the user almost always wants:
-**author a workflow → wrap it in a member → group members into a team → publish as a
-service (register to NyxID) → schedule it.**
+The first tree is a common build-and-operate path. The second is a separate
+Profile authority. Creating or publishing either one does not create, convert,
+or bind the other.
@@
-Most of this family is **plain REST you call as a client** through the NyxID broker above
+Most build-and-operate skills use **plain REST as a client** through the NyxID broker above
@@
-exception is **`aevatar-workflow-authoring`**, written for the model running *inside* an aevatar
+first exception is **`aevatar-workflow-authoring`**, written for the model running *inside* an aevatar
@@
-endpoints (they are not).
+endpoints (they are not).
+
+The second exception is `aevatar-agent-profile-management`. In-session mode is
+available only when the exact `agent_profiles` tool is present. Client REST
+mode first reads `/api/openapi.json` and requires the complete eight-pair
+Profile route family. If neither capability exists, it stops without replacing
+the request with a workflow, team, service, or schedule.
@@
 | **Triage a failure** — is it an aevatar / nyxid / ornn problem? read the code, then file an issue or get authoritative usage guidance (use AFTER something breaks) | `aevatar-triage` | reads repos via `gh` or `nyxid_proxy` `api-github`; `gh issue` |
+| Create/read/edit/validate/publish an **Agent Profile**; manage exact Ornn skills, activation/routing, ETag, and tool ceilings | `aevatar-agent-profile-management` | exact `agent_profiles` tool, or the complete live `/api/scopes/{scopeId}/agent-profiles...` REST family |
@@
-ornn has no separate "collection" object — the aevatar capability set is held together by
-a shared **`aevatar` tag** and indexed by this map. An ornn skill search for **`aevatar`**
-returns the whole family as one set; load whichever member you need with `use_skill`. This
-map is the canonical entry point; the rest are pulled on demand.
+Ornn has a real, versioned `aevatar-platform` **skillset** with immutable
+revisions and master instructions. Its exact member pins are the curated
+collection authority. The shared `aevatar` tag remains useful for discovery,
+but search results are not a substitute for the skillset revision or closure.
+This map is the semantic entry point; load the focused member that owns the
+request.
@@
 **Build & operate — the control-plane family** (client REST, `category: plain`, public)
@@
 - `aevatar-scheduler` — cron schedules that fire a service (scope-owner NyxID auth).
+
+**Agent Profile — independent authority surface**
+- `aevatar-agent-profile-management` — capability-detect Profile management;
+  manage owner draft content, exact Ornn skill bindings, activation/routing,
+  tool ceilings, validation, ETag mutation, publication, and honest reread.
@@
-## The golden path, end to end
+## One build-and-operate path
@@
 6. **Schedule** it on a cron, authenticated as the scope owner — `aevatar-scheduler`; or
@@
    proxy with a NyxID API key — `aevatar-service-publisher`.
+
+Agent Profile management is not step 7. Route it independently to
+`aevatar-agent-profile-management`. Profile publication does not bind this
+build-and-operate path.
@@
-- **You are a client.** Everything here is plain REST you call with the user's NyxID
-  bearer token. There is no server-side tool that creates teams/members/services for you —
-  you make the HTTP calls.
+- **Use only the real surface.** Team/member/service work is client REST;
+  workflow authoring and Agent Profile management may expose exact in-session
+  tools. Never invent a tool or call a tool name as an HTTP endpoint.
+- **Profile capability is deployment-specific.** Require exact `agent_profiles`
+  in-session or the full live OpenAPI family in REST mode. A resource `404` is
+  not the deployment probe.
+- **Profile mutations are versioned and asynchronous.** Preserve the strong
+  ETag, use `If-Match`, treat `202` as accepted-only, and reread the owner model
+  for authority version, operation status, revisions, and digests.
+- **Profile execution is narrow.** Today only Host-selected newly created NyxID
+  direct conversations consume a Profile. Existing chats and Workflow, Studio,
+  relay, Channel, Scheduled, AgentRun, services, and schedules do not.
*** End Patch
```

- [ ] **Step 3: Validate and run GREEN against the semantic router patch**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-platform-map 1.8
rg -n 'two resource surfaces|aevatar-agent-profile-management|full live OpenAPI|accepted-only|Host-selected|versioned `aevatar-platform`' \
  "$candidate/SKILL.md"
! rg -n 'ornn has no separate "collection" object' "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-platform-map-suite.sh" candidate "$candidate"
```

Expected: all four prompts run with variant `candidate`; required rules `C1`,
`C2`, `C7`, `C8`, and `C9` pass. The map routes rather than performing the
management operation itself.

- [ ] **Step 4: Add the explicit router red flag**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-platform-map/SKILL.md
@@
 | **Invoke**, watch **runs**, observe | (this map + service-publisher's invoke section) | `/invoke/{endpointId}`, `/runs/*`, `/api/workflow/observatory/*` |
+
+**Profile routing red flag:** never route “agent purpose/personality,
+instructions, exact skills, activation/routing, Profile tool ceiling, Profile
+validation/publication” to workflow authoring, team builder, service publisher,
+or scheduler. If Profile capability is absent, the correct result is an honest
+unavailable report or a proposed draft—not resource substitution.
-
+
 If a companion skill is not already loaded, find it with an ornn skill search for the
*** End Patch
```

- [ ] **Step 5: Validate and run REFACTOR map evaluations**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-platform-map 1.8
rg -n 'two resource surfaces|aevatar-agent-profile-management|full live OpenAPI|accepted-only|Host-selected|versioned `aevatar-platform`' \
  "$candidate/SKILL.md"
! rg -n 'ornn has no separate "collection" object' "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-platform-map-suite.sh" hardened "$candidate"
```

Expected: all four prompts run with variant `hardened`; required rules `C1`,
`C2`, `C7`, `C8`, and `C9` pass. Compare with the saved `candidate` scorecard
and confirm the red-flag section closes rationalizations without changing
routing ownership.

- [ ] **Step 6: Publish and exact-read `1.8`**

Run validation, then publish once:

```bash
candidate="$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map"
"$WORK_ROOT/bin/release-ornn-skill.sh" \
  validate update \
  aevatar-platform-map 1.8 \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  1.7 10ec2384b9c200594d20a0483237e8d0f77b56f917e802fe38fed93b904ac683 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102

map_release="$("$WORK_ROOT/bin/release-ornn-skill.sh" \
  publish update \
  aevatar-platform-map 1.8 \
  b8bf9e98-2658-4e09-9c51-2e4958137091 \
  1.7 10ec2384b9c200594d20a0483237e8d0f77b56f917e802fe38fed93b904ac683 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102)"
jq -e '.stage == "published-and-verified" and .version == "1.8" and .isPrivate == false' \
  <<<"$map_release" >/dev/null
printf '%s\n' "$map_release" \
  > "$WORK_ROOT/readback/aevatar-platform-map-1.8.release.json"
```

Expected: stable GUID/owner, public `1.8`, exact JSON equality, and
byte-identical ZIP readback.

---

### Task 4: Upgrade and Publish `aevatar-feasibility-advisor@1.2`

**Files:**

- Modify: `$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor/SKILL.md`
- Create: `$WORK_ROOT/bin/run-feasibility-advisor-suite.sh`
- Test: `$WORK_ROOT/evals/aevatar-feasibility-advisor/**`
- Publish: GUID `d0619556-402e-4baf-aa26-fbfe78ac937c`, version `1.2`

**Interfaces:**

- Consumes: verified management `1.0`, map `1.8`, and exact advisor `1.1`.
- Produces: feasibility guidance that separates management availability,
  publication readiness, rollout admission, and runtime consumption.

- [ ] **Step 1: Create the advisor suite and run RED against exact `1.1`**

Use `apply_patch` to create `$WORK_ROOT/bin/run-feasibility-advisor-suite.sh`
with this exact content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: $0 <variant> <source-dir>" >&2
  exit 64
fi

variant="$1"
source_dir="$2"

run_case() {
  "$WORK_ROOT/bin/run-skill-eval.sh" aevatar-feasibility-advisor \
    "$1" "$variant" "$source_dir" "$2"
}

run_case capability-absent \
  'Can Aevatar create a Profile that gives my agent purpose, instructions, and routed Ornn skills today? The current session has no agent_profiles tool and live OpenAPI has no agent-profiles path. Give a yes/no answer and next step.'
run_case universal-consumption \
  'Can I publish a Profile and have my existing direct chat, Lark channel, workflow, schedule, and AgentRun all start using it today?'
run_case schedule-consumer \
  'I need Profile support-profile to control existing schedule sched-alpha. We already built the workflow and the host will not change code. Is this self-service, host-gated, or unsupported in the current consumer boundary?'
run_case accepted-versus-execution \
  'The full Profile route family exists. A publish returned 202. Is Profile execution feasible now, and what independent facts remain unproven?'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-feasibility-advisor-suite.sh"
bash -n "$WORK_ROOT/bin/run-feasibility-advisor-suite.sh"
old="$WORK_ROOT/baselines/aevatar-feasibility-advisor/1.1/aevatar-feasibility-advisor"
"$WORK_ROOT/bin/run-feasibility-advisor-suite.sh" old "$old"
```

Expected RED: `1.1` has no Profile feasibility dimension and is likely to
route into the build/schedule chain or overstate consumption.

- [ ] **Step 2: Apply the Profile feasibility patch**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-feasibility-advisor/SKILL.md
@@
-description: Decide — honestly — whether a thing the user wants to build on Aevatar is possible, what its prerequisites are, or why it cannot be done, BEFORE anyone starts building. Use this first whenever a user describes a goal rather than a concrete artifact — "can aevatar do X", "I want a bot that…", "build me something that posts to Twitter / reads my GitHub / replies on Telegram", "is it possible to…", "automate … every day", "let Lark Base trigger a workflow". It teaches the one hard premise (every third-party capability is brokered by NyxID), the two distinct surfaces (outbound connector vs inbound channel), external HTTP trigger options such as Lark Base automation, how to check what is actually connectable, the prerequisite for each capability class, what is host-gated (and so not self-serve), and what is genuinely impossible without new NyxID/Aevatar platform work — so you can negotiate scope and give the user a straight answer plus next steps instead of over-promising. It scopes; it does not build (hand off to workflow-authoring / team-builder / service-publisher / scheduler).
-version: "1.1"
+description: Use before building when a caller asks whether an Aevatar goal is possible, what it requires, who must act, or what current alternative exists—including external connectors/channels, workflows/services/schedules, and Agent Profile management, publication, rollout, or runtime consumption.
+version: "1.2"
@@
     - negotiation
+    - agent-profile
@@
-`aevatar-workflow-authoring` → `aevatar-team-builder` → `aevatar-service-publisher` →
-`aevatar-scheduler` (see `aevatar-platform-map`).
+the focused skill chosen by `aevatar-platform-map`. The workflow/team/service/
+schedule path is one surface; Agent Profile management is an independent
+surface owned by `aevatar-agent-profile-management`.
@@
 4. **Report honestly** with the template at the end: possible + prereqs, or host-action-needed,
    or not-feasible + alternative.
+5. **For Agent Profile goals, apply all four Profile gates below.** Management,
+   publication, rollout admission, and runtime consumption are separate facts.
@@
 | **Exactly-once** external side effects (e.g. "charge exactly once") | ❌ Not guaranteed | The workflow saga is **at-least-once** with idempotency keys. Require an idempotent connector endpoint, or do the exactly-once elsewhere. |
+| Create or edit an **Agent Profile** | ✅ Only when the current surface advertises it | In-session requires the exact `agent_profiles` tool. REST requires every method/path in the complete Profile OpenAPI family before mutation. If absent, only prepare a proposed draft; do not substitute a workflow/team/service. |
+| Validate or publish a Profile with Ornn skills | ✅ When management exists and exact dependencies resolve | Every binding needs GUID, literal major.minor version, expected name, and expected publisher. Versioned mutation needs the current strong ETag. A `202` is dispatch accepted, not publication proof. |
+| Make an existing chat, workflow, channel, schedule, AgentRun, or arbitrary service consume a Profile | ❌ Not in the current consumer boundary | Existing conversations do not hot-upgrade. Workflow, Studio, relay, Channel, Scheduled, AgentRun, arbitrary services, and schedules are not current Profile consumers. Do not invent a binding operation. |
+| Use a Profile in a newly created NyxID direct conversation | ⚠️ Host-rollout-selected only | The Host must admit the new conversation against its reviewed Profile and rollout pins. Publishing an arbitrary owner Profile does not enroll it. |
+
+## Agent Profile feasibility has four independent gates
+
+1. **Management availability:** exact `agent_profiles` tool, or the complete
+   live REST family. A Profile `404` is not the deployment probe.
+2. **Publication readiness:** canonical draft valid; exact Ornn GUID, literal
+   version, name, publisher, package, routing, and tool-policy subset verified;
+   current ETag used; accepted mutation reconciled in the owner read model.
+3. **Host admission:** the Host-owned rollout selects a reviewed Profile,
+   revision, digest, and exact closure for a new NyxID direct conversation.
+4. **Runtime consumption:** only that newly created admitted direct conversation
+   persists the immutable binding. Publication alone proves none of gates 3–4.
+
+If the ask fails a later gate, do not report the earlier gate as useless. A
+Profile can be valid and published while not admitted to any runtime; that is
+an honest, expected state.
@@
 - **Async settling.** Bindings/deployments/runs are eventually consistent — never promise a
   result from a 2xx alone.
+- **Profile ACKs are explicit.** `202 Accepted` is dispatch admission only;
+  owner-management reread evidence is required before saying committed or
+  published.
@@
 - **Missing connector / new shape / new channel** → this is NyxID/Aevatar **platform work**;
@@
   offer the closest feasible alternative.
+- **Agent Profile unavailable** → report which capability check failed and
+  hand off only a proposed draft. Do not turn the request into a workflow,
+  service, schedule, or claim that publication binds a consumer.
@@
 - **Never promise host-gated outcomes** (NyxID registration, anything needing host config) or
   features that need platform work — surface them as dependencies, not done deals.
+- **Profile management ≠ publication ≠ Host admission ≠ consumption.** Report
+  each gate independently.
+- **No Profile schedule binding.** A schedule may invoke a service; it does not
+  consume a Profile under the current contract.
*** End Patch
```

- [ ] **Step 3: Validate and run GREEN against the feasibility patch**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-feasibility-advisor 1.2
rg -n 'four independent gates|agent_profiles|Host-rollout-selected|No Profile schedule binding|202 Accepted' \
  "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-feasibility-advisor-suite.sh" candidate "$candidate"
```

Expected: all four prompts run with variant `candidate`; required rules `C1`,
`C6`, `C7`, `C8`, `C9`, and `C10` pass.

- [ ] **Step 4: Add the feasibility rationalization counter**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-feasibility-advisor/SKILL.md
@@
 ## Honesty rules
-
+
+- Source code or a feature branch does not make Profile management feasible on
+  a running deployment. The live tool/OpenAPI capability surface wins.
+- A successful Profile publication is not evidence that any current or future
+  conversation has been selected by Host rollout.
 - **Check the live catalog/services** before claiming a connector exists or not. Examples in
*** End Patch
```

- [ ] **Step 5: Validate and run REFACTOR advisor evaluations**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-feasibility-advisor 1.2
rg -n 'four independent gates|agent_profiles|Host-rollout-selected|No Profile schedule binding|202 Accepted' \
  "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-feasibility-advisor-suite.sh" hardened "$candidate"
```

Expected: all four prompts run with variant `hardened`; required rules `C1`,
`C6`, `C7`, `C8`, `C9`, and `C10` pass. Compare with the saved `candidate`
scorecard and confirm hardening changes no feasibility gate.

- [ ] **Step 6: Publish and exact-read `1.2`**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor"
"$WORK_ROOT/bin/release-ornn-skill.sh" \
  validate update \
  aevatar-feasibility-advisor 1.2 \
  d0619556-402e-4baf-aa26-fbfe78ac937c \
  1.1 a8ddcf50cffa3d4577a6a2624660599115f294ec6f9770222c233d9ad82341a4 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102

feasibility_release="$("$WORK_ROOT/bin/release-ornn-skill.sh" \
  publish update \
  aevatar-feasibility-advisor 1.2 \
  d0619556-402e-4baf-aa26-fbfe78ac937c \
  1.1 a8ddcf50cffa3d4577a6a2624660599115f294ec6f9770222c233d9ad82341a4 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102)"
jq -e '.stage == "published-and-verified" and .version == "1.2" and .isPrivate == false' \
  <<<"$feasibility_release" >/dev/null
printf '%s\n' "$feasibility_release" \
  > "$WORK_ROOT/readback/aevatar-feasibility-advisor-1.2.release.json"
```

Expected: stable GUID/owner, public `1.2`, exact JSON equality, and
byte-identical ZIP readback.

---

### Task 5: Upgrade and Publish `aevatar-triage@1.4`

**Files:**

- Modify: `$WORK_ROOT/candidates/aevatar-triage/aevatar-triage/SKILL.md`
- Create: `$WORK_ROOT/bin/run-triage-suite.sh`
- Test: `$WORK_ROOT/evals/aevatar-triage/**`
- Publish: GUID `fbd40315-317f-4f80-9885-b44b83e1a204`, version `1.4`

**Interfaces:**

- Consumes: verified management `1.0`, map `1.8`, advisor `1.2`, and exact
  triage `1.3`.
- Produces: Profile-specific deployment, resource, concurrency, validation,
  dependency, ACK, and consumer-boundary diagnostics.

- [ ] **Step 1: Create the triage suite and run RED against exact `1.3`**

Use `apply_patch` to create `$WORK_ROOT/bin/run-triage-suite.sh` with this exact
content:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: $0 <variant> <source-dir>" >&2
  exit 64
fi

variant="$1"
source_dir="$2"

run_case() {
  "$WORK_ROOT/bin/run-skill-eval.sh" aevatar-triage \
    "$1" "$variant" "$source_dir" "$2"
}

run_case deployment-404 \
  'Production Agent Profile create returns 404. A local feature branch contains the endpoint implementation, but live /api/openapi.json has no agent-profiles path. Attribute the failure and state the next probe.'
run_case resource-404 \
  'Live OpenAPI contains the complete Profile route family, but GET owner Profile support-profile returns 404. Is this deployment absence, a platform defect, or a resource/visibility result?'
run_case accepted-not-visible \
  'Profile publish returned 202 accepted with op-71, then the UI still shows publishedRevision 2. The team wants to file an Aevatar defect immediately. What evidence and verdict do you report?'
run_case stale-etag \
  'A draft skill PUT returns 412 after another editor changed the Profile. The binding body is exact and idempotency key stable. Should we retry it as-is?'
run_case validation-versus-dependency \
  'Publish returns 422 ORNN_EXACT_REFERENCE_MISMATCH in one environment and 503 ORNN_DEPENDENCY_UNAVAILABLE in another. Separate the two diagnoses and the safe next action.'
run_case unsupported-consumers \
  'Revision 3 is published but sched-alpha, an existing direct chat, a Lark channel, Workflow chat, and AgentRun do not use it. Decide whether publication failed.'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-triage-suite.sh"
bash -n "$WORK_ROOT/bin/run-triage-suite.sh"
old="$WORK_ROOT/baselines/aevatar-triage/1.3/aevatar-triage"
"$WORK_ROOT/bin/run-triage-suite.sh" old "$old"
```

Expected RED: `1.3` has only a generic `404` registry rule and no Profile
capability/resource split, typed Profile errors, or consumer-boundary result.

- [ ] **Step 2: Apply the Profile diagnostic patch**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-triage/SKILL.md
@@
-description: Use AFTER something goes wrong while using Aevatar — a user hits an error, failure, or confusing behavior and you must find whether it lives in Aevatar, NyxID, or Ornn, then act. Triggers - "aevatar is erroring", "why did my workflow fail", "my scheduled run did not fire", "my bot does not reply", "connector 401/403", "skill won't pull/upload", "is this an aevatar, nyxid, or ornn bug", "file an issue", "am I using this right". It attributes the failure by tracing the request path, pulls that layer's real public source for a code-grounded root cause citing file and line, then branches - draft and, only on explicit user confirmation, file a precise GitHub issue when behavior violates the layer's published contract, or explain the correct usage from the code when it is a usage mistake. The after-it-breaks counterpart to aevatar-feasibility-advisor; never auto-files, de-dups first, never claims a root cause without a code citation. Works locally (git + gh) and server-side (nyxid_proxy + api-github).
-version: "1.3"
+description: Use after an Aevatar, NyxID, or Ornn operation fails or behaves unexpectedly—including workflow, schedule, channel, connector, skill, and Agent Profile capability, 404, ETag, validation, dependency, publication, or runtime-consumption symptoms—and the caller needs evidence-based attribution, correct usage, or a confirmation-gated defect report.
+version: "1.4"
@@
     - support
+    - agent-profile
@@
-| **Aevatar** | `aevatarAI/aevatar` (C#/.NET) | agent runtime + tool execution, workflow engine, channels, CQRS/projection + readmodels, control-plane REST, scheduler validation | workflow validate / draft-run / run failures, member-team-service binding stuck (async never `succeeded`), the **aevatar side** of a channel bot, stale readmodel / observatory, scheduled run that stops firing, control-plane 4xx/5xx |
+| **Aevatar** | `aevatarAI/aevatar` (C#/.NET) | agent runtime + tool execution, workflow engine, channels, CQRS/projection + readmodels, control-plane REST, Agent Profile authority/application/Host rollout, scheduler validation | workflow validate / draft-run / run failures, member-team-service binding stuck, the **aevatar side** of a channel bot, Profile capability/ETag/publication/consumption symptoms, stale readmodel / observatory, scheduled run that stops firing, control-plane 4xx/5xx |
@@
-skill name, `commandId`/`correlationId`, schedule fire-record fields (`fireCount` / `lastFireAt` /
+skill name, Profile `profileId` / `operationId` / `commandId` / `correlationId` /
+authority state version / draft and published revisions and digests, schedule fire-record fields (`fireCount` / `lastFireAt` /
@@
 | `404` on a thing you reference | **whichever registry owns it** | skill -> Ornn; connector/service -> NyxID; team/member/scope -> Aevatar |
+| Profile request but exact `agent_profiles` tool is absent | **Current Aevatar session capability** | inventory the exact tool surface; do not infer capability from another tool or skill |
+| Live OpenAPI lacks one or more required Profile method/path pairs | **Running Aevatar deployment capability** | compare the live document with the complete eight-pair family; stop before mutation; feature-branch source is not deployment proof |
+| Profile route returns `404` after the complete family is present | **Aevatar Profile resource/visibility** | owner vs discovery route, exact `scopeId`, separate `ownerHandle` and `profileSlug`, caller visibility; do not relabel it undeployed capability |
+| Profile mutation returns `428` or malformed If-Match `400` | **Client precondition usage** | owner GET strong ETag present? did the transport retain the response header verbatim? |
+| Profile mutation returns `412 AGENT_PROFILE_STALE_VERSION` | **Expected optimistic concurrency conflict** | reread the full owner model, compare authority version/draft, reconstruct the mutation, and use the new ETag; never blind-replay |
+| Profile validate/publish returns `422` | **Aevatar validation over draft + exact Ornn sealing** | inspect typed diagnostic path/code: draft shape, four exact Ornn facts, activation/routing, tool subset, or package seal |
+| Profile operation returns `503` | **Declared dependency/dispatch boundary** | distinguish Ornn resolution, ingress proof, and Actor dispatch; preserve operation/idempotency facts and do not claim commit |
+| Profile mutation returns `202` | **Aevatar dispatch admission only** | correlate operation id, then reread owner authority version, last mutation status, draft/published revisions, source draft digest, and snapshot digest |
+| Published Profile is unused by an existing chat, workflow, channel, schedule, AgentRun, or arbitrary service | **Expected current consumer boundary** | only Host-selected newly created NyxID direct conversations consume Profiles; publication is not runtime binding |
@@
 **Do not stop at the first match.** Gather the disambiguating evidence and *eliminate* — a plausible
 first guess that you haven't excluded the alternatives for is not an attribution.
+
+### Agent Profile diagnostic order
+
+1. Pin the running image/commit and read the live tool list or OpenAPI. Do not
+   use a local or remote feature branch to claim a route is deployed.
+2. For REST, require the complete route family before interpreting resource
+   responses. For in-session management, require exact `agent_profiles`.
+3. Keep `profileId`, `workflowId`, `memberId`, `publishedServiceId`, and
+   conversation Actor id separate in every trace.
+4. For accepted mutations, follow `operationId` from receipt to the owner read
+   model and compare authoritative versions/digests. `202` is not a terminal
+   success state.
+5. Apply the contract test only after steps 1–4. Missing capability or an
+   unsupported consumer is a product/deployment boundary, not automatically a
+   defect.
@@
-  connector adapters in `src/Aevatar.AI.ToolProviders.*` (incl. NyxId, Ornn); readmodels in
-  `src/Aevatar.CQRS.Projection.*`; engine + HTTP/OpenAPI in `src/workflow/`; **contract** in
-  `src/Aevatar.AI.Abstractions` + `docs/canon/`; errors as workflow exceptions + control-plane 4xx/5xx.
+  connector adapters in `src/Aevatar.AI.ToolProviders.*` (incl. NyxId, Ornn);
+  Agent Profile contracts/application/Host endpoints under the current
+  `Aevatar.GAgentService.*` and `Aevatar.AI.*` Profile areas; readmodels in
+  `src/Aevatar.CQRS.Projection.*`; engine + HTTP/OpenAPI in `src/workflow/`;
+  **contract** in `src/Aevatar.AI.Abstractions` + `docs/canon/`; errors as
+  typed Profile/control-plane results and workflow exceptions.
@@
 - **Dispatch success ≠ real-world effect.** A climbing `fireCount` or a `200` proxy body can still
   mean nothing happened — verify the actual side-effect out-of-band.
+- **Profile `202` ≠ committed publication.** Require owner-read-model operation,
+  authority version, revision, and digest evidence.
+- **Profile publication ≠ consumer binding.** Unsupported consumers and
+  existing conversations remaining unchanged are expected boundaries.
+- **Capability before resource diagnosis.** Probe the exact tool or complete
+  OpenAPI family once; only then interpret Profile `404`.
*** End Patch
```

- [ ] **Step 3: Validate and run GREEN against the Profile diagnostic patch**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-triage/aevatar-triage"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-triage 1.4
rg -n 'Agent Profile diagnostic order|AGENT_PROFILE_STALE_VERSION|Profile `202`|feature branch|Host-selected' \
  "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-triage-suite.sh" candidate "$candidate"
```

Expected: all six prompts run with variant `candidate`; required rules `C1`,
`C2`, `C5`, `C6`, `C7`, and `C8` pass. A `422` is a validation verdict; a
`503` is availability/dispatch evidence; neither may be collapsed into the
other.

- [ ] **Step 4: Add the deployment-branch hardening rule**

Use `apply_patch` with this exact patch:

```diff
*** Begin Patch
*** Update File: aevatar-triage/SKILL.md
@@
 ## Honesty & safety rails
-
+
+- **A branch containing Agent Profile code is not live capability.** A root
+  cause may cite that branch only after its commit is proven to match the
+  deployed image. Live OpenAPI/tool absence overrides assumptions from source.
+- **Stable binding or idempotency identity does not legalize stale replay.** A
+  `412` requires reread and intent reconstruction against current state.
 - **Never auto-file.** Always: de-dup -> draft -> explicit user confirmation -> file.
*** End Patch
```

- [ ] **Step 5: Validate and run REFACTOR triage evaluations**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-triage/aevatar-triage"
"$WORK_ROOT/bin/validate-ornn-skill.rb" \
  "$candidate" aevatar-triage 1.4
rg -n 'Agent Profile diagnostic order|AGENT_PROFILE_STALE_VERSION|Profile `202`|feature branch|Host-selected' \
  "$candidate/SKILL.md"
"$WORK_ROOT/bin/run-triage-suite.sh" hardened "$candidate"
```

Expected: all six prompts run with variant `hardened`; required rules `C1`,
`C2`, `C5`, `C6`, `C7`, and `C8` pass. Compare with the saved `candidate`
scorecard and confirm hardening changes no diagnostic ownership or failure
classification.

- [ ] **Step 6: Publish and exact-read `1.4`**

Run:

```bash
candidate="$WORK_ROOT/candidates/aevatar-triage/aevatar-triage"
"$WORK_ROOT/bin/release-ornn-skill.sh" \
  validate update \
  aevatar-triage 1.4 \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  1.3 4ac7270d401e5245e9fb762d551120e5628436ef159d1cdc187b4ef4b0c3d388 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102

triage_release="$("$WORK_ROOT/bin/release-ornn-skill.sh" \
  publish update \
  aevatar-triage 1.4 \
  fbd40315-317f-4f80-9885-b44b83e1a204 \
  1.3 4ac7270d401e5245e9fb762d551120e5628436ef159d1cdc187b4ef4b0c3d388 \
  "$candidate" \
  5d0d7b72-acff-49af-bb1b-9f30bbb7c102)"
jq -e '.stage == "published-and-verified" and .version == "1.4" and .isPrivate == false' \
  <<<"$triage_release" >/dev/null
printf '%s\n' "$triage_release" \
  > "$WORK_ROOT/readback/aevatar-triage-1.4.release.json"
```

Expected: stable GUID/owner, public `1.4`, exact JSON equality, and
byte-identical ZIP readback.

---

### Task 6: Publish the Exact 13-Member `aevatar-platform` Revision

**Files:**

- Create: `$WORK_ROOT/aevatar-platform-master.md`
- Publish: skillset GUID `248b99d6-36ff-4d41-bb45-baa25c6a9cad`

**Interfaces:**

- Consumes: four independently verified public skill versions and the exact
  `1.11` predecessor.
- Produces: one server-assigned immutable revision with 13 exact direct members
  and updated master routing instructions.

- [ ] **Step 1: Re-read every exact target and require exact release hashes**

Run:

```bash
while IFS=$'\t' read -r name version expected_guid expected_hash; do
  detail="$(nyxid proxy request ornn-api "/api/v1/skills/$expected_guid?version=$version" \
    --method GET --output json)"
  jq -e \
    --arg name "$name" \
    --arg version "$version" \
    --arg guid "$expected_guid" \
    --arg hash "$expected_hash" \
    --arg owner "5d0d7b72-acff-49af-bb1b-9f30bbb7c102" '
      .error == null
      and .data.name == $name
      and .data.version == $version
      and .data.guid == $guid
      and .data.createdBy == $owner
      and .data.isPrivate == false
      and .data.skillHash == $hash
    ' <<<"$detail" >/dev/null
done <<TARGETS
fallback-to-calling-agent	1.0	5f0fa2d8-55f2-4049-a1b0-f0722fcba7a2	eb7d90688f02c96a3a1bf59aedca5e970c57c3d70cc0ff20144f9548ce7a680e
aevatar-workflow-authoring	1.5	bdfb0ec1-41cc-4909-815a-eb1a12b7aa2e	e12be215601643959ab6f24edc1bf5449ccb98ef7cf0db80c64a641d0d15903c
aevatar-team-builder	1.3	6587bde4-6acc-4acb-8152-1dbbbe154e72	977a00cf610fd3c310d694084c4a373bf6ca9c5aae2ba8a1052a5fb2a199eb7e
aevatar-scheduler	1.7	8d4bb4e0-81e8-472b-bd71-2777130aba2f	8d56f22fbc7bb82cbdd9c1b777070ef7c14eaf3c19938b4002786380c246395a
aevatar-service-publisher	1.5	b753047e-ad14-4d85-a12a-ca68534d9e20	e35e34b4b4b70eacfff1e1c4333157f7a91631af87eeed5a9a2d6c22ddccdb63
aevatar-platform-map	1.8	b8bf9e98-2658-4e09-9c51-2e4958137091	$(jq -er '.skillHash' "$WORK_ROOT/readback/aevatar-platform-map-1.8.release.json")
aevatar-feasibility-advisor	1.2	d0619556-402e-4baf-aa26-fbfe78ac937c	$(jq -er '.skillHash' "$WORK_ROOT/readback/aevatar-feasibility-advisor-1.2.release.json")
aevatar-triage	1.4	fbd40315-317f-4f80-9885-b44b83e1a204	$(jq -er '.skillHash' "$WORK_ROOT/readback/aevatar-triage-1.4.release.json")
firecrawl-via-nyxid	1.1	47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2	e5d73ef299353781f287828f1c6a01fb3aaff678b17b80c733cfc2302c696851
github-via-nyxid	1.0	abd23ac2-ed1c-4f8c-a6bc-6270390cbe32	75354416d12d366d30118bbf4a6469b4c3a9ee70726c2dcd4dba4abb9c87a83e
aevatar-automation	1.1	1f8e2f07-67d3-4ac8-b7de-4596f36f4634	0bb4f61391e185df608208b01708c1bf9c485d6621fedfdcbf3f09ee0594bad3
aevatar-channels-delivery	1.1	d2b575d3-0d80-4167-99e4-6161be47db7f	ce749f1d9459d6444d673ead42aade50bf061ce0e47b16dfc4259160e6c71d08
TARGETS

management_guid="$(jq -er '.guid' \
  "$WORK_ROOT/readback/aevatar-agent-profile-management-1.0.release.json")"
management_hash="$(jq -er '.skillHash' \
  "$WORK_ROOT/readback/aevatar-agent-profile-management-1.0.release.json")"
management="$(nyxid proxy request ornn-api \
  "/api/v1/skills/$management_guid?version=1.0" \
  --method GET --output json)"
jq -e --arg guid "$management_guid" --arg hash "$management_hash" '
  .error == null
  and .data.name == "aevatar-agent-profile-management"
  and .data.guid == $guid
  and .data.version == "1.0"
  and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .data.isPrivate == false
  and .data.skillHash == $hash
' <<<"$management" >/dev/null
```

Expected: all 13 exact roots are readable and public. The nine unchanged pins
must retain their `1.11` refs/GUIDs/versions/hashes; the four released members
must equal their saved release evidence exactly.

- [ ] **Step 2: Preflight the combined dependency closure without writing**

Run:

```bash
closures=()
while IFS=$'\t' read -r name version; do
  one="$(nyxid proxy request ornn-api \
    "/api/v1/skills/$name/closure?version=$version" \
    --method GET --output json)"
  jq -e '.error == null' <<<"$one" >/dev/null
  closures+=("$one")
done <<'ROOTS'
fallback-to-calling-agent	1.0
aevatar-workflow-authoring	1.5
aevatar-team-builder	1.3
aevatar-scheduler	1.7
aevatar-service-publisher	1.5
aevatar-platform-map	1.8
aevatar-agent-profile-management	1.0
aevatar-feasibility-advisor	1.2
aevatar-triage	1.4
firecrawl-via-nyxid	1.1
github-via-nyxid	1.0
aevatar-automation	1.1
aevatar-channels-delivery	1.1
ROOTS

printf '%s\n' "${closures[@]}" | jq -s -e '
  [.[].data.items[]]
  | group_by(.name)
  | all(.[]; ([.[].version] | unique | length) == 1)
' >/dev/null
```

Expected: every root closure resolves and no dependency name resolves to two
literal versions. Any conflict stops before skillset publication.

- [ ] **Step 3: Reassert the skillset concurrency boundary**

Run the Task 1 exact `aevatar-platform@1.11` detail and closure assertions
again immediately before composing the request. Also assert the latest version
list still begins with `1.11`:

```bash
versions="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/aevatar-platform/versions' \
  --method GET --output json)"
jq -e '.error == null and .data.items[0].version == "1.11"' \
  <<<"$versions" >/dev/null
```

Expected: no intervening revision. If the latest is no longer `1.11`, stop and
audit the new revision; do not overwrite another release's members or master
instructions.

- [ ] **Step 4: Write the exact master instructions**

Use `apply_patch` to create `$WORK_ROOT/aevatar-platform-master.md` with this
exact content:

```markdown
# Aevatar platform router

Choose the authoritative resource before taking an action. Aevatar exposes two
independent product surfaces:

```text
build and operate:
  scope -> team -> member (workflow | script | gagent)
        -> published service -> schedule / external trigger

Agent Profile:
  ownerHandle/profileSlug -> opaque profileId -> draft -> published snapshot
```

The first surface defines and operates callable business capabilities. The
second defines an agent's purpose, instructions, exact Ornn skill routing,
maximum tool policy, and recovery policy. A Profile is not a workflow stage or
an alias for a team member, service, schedule, or conversation.

`aevatar-platform` is a dynamic routing collection: its newest revision may
change as focused skills evolve. It is not a Profile publication reference or
trust closure. A Profile continues to bind exact individual Ornn skill GUIDs,
literal versions, canonical names, and publisher ids; never bind this whole
skillset or infer a Profile closure from its current members.

Keep these identities separate:

- `profileId`: opaque Agent Profile authority identity;
- `workflowId`: workflow draft/definition identity;
- `memberId`: Studio team member authority;
- `publishedServiceId`: callable service runtime identity; and
- conversation Actor id: one runtime conversation.

Never convert them by equality, prefix, route position, or lifecycle inference.

## Route to one focused member

- Start with `aevatar-platform-map` when the resource or caller mode is
  unclear.
- Use `aevatar-feasibility-advisor` before non-trivial work to separate what is
  self-service, host-gated, unsupported, or unavailable on the running
  deployment.
- Use `aevatar-agent-profile-management` for Profile create/read/draft,
  purpose/instructions, exact Ornn skill bindings, activation/routing, Profile
  or recovery tool ceilings, ETag mutation, validation, publication, and
  Profile-specific usage guidance.
- Use `aevatar-triage` after a failure to pin the deployed image, trace the
  Aevatar/NyxID/Ornn boundary, distinguish capability from resource results,
  and give code-grounded guidance or a confirmation-gated defect draft.
- Use `aevatar-workflow-authoring` for runnable workflow YAML and its real
  in-session or client draft-run/publish surface.
- Use `aevatar-team-builder` for scope-owned teams and member authorities.
- Use `aevatar-service-publisher` for publishing a member/team as a callable
  service, verifying host-gated NyxID exposure, and invoking it.
- Use `aevatar-scheduler` for generic recurring service/envelope scheduling.
- Use `aevatar-automation` for independent scheduled skill agents and its
  in-session automation tools.
- Use `aevatar-channels-delivery` for channel registration, delivery targets,
  and in-session delivery/tool credential boundaries.
- Use `firecrawl-via-nyxid` or `github-via-nyxid` only for the external
  capability each one owns.
- Use `fallback-to-calling-agent` only after a genuine server-side attempt
  reaches a hard boundary that the caller's environment must finish.

## Agent Profile capability gate

Before any Profile operation, select one real surface:

1. In-session mode requires the exact `agent_profiles` tool in the current
   tool list. Do not infer it from another Aevatar tool, Ornn access, this
   skillset, or a system prompt.
2. Client REST mode reads live `GET /api/openapi.json` and requires every
   method/path in the complete Profile family: owner create/get, draft update,
   exact skill PUT/DELETE, validate, publish, and public discovery.

If the selected surface is absent or partial, stop before mutation. Report
that the running session/deployment does not expose complete Profile
management. A proposed draft is allowed; claiming creation, validation,
publication, or binding is not. Never fall back to workflow, team, member,
service, or schedule creation.

A Profile-resource `404` is not the capability probe. After capability is
established, `404` means that Profile is missing or invisible to the caller.

## Profile mutation and publication rules

- Owner management reads return a strong ETag. Preserve it verbatim and use it
  for versioned mutation (`etag` in the exact `agent_profiles` tool contract;
  `If-Match` over REST). A REST transport must both expose ETag and forward
  `If-Match`. The current generic NyxID CLI/proxy path fails that combined
  requirement and is read/proposal-only for Profile mutation.
- On `412`, reread the full owner model, reconstruct the intended mutation
  against current state, and use the new ETag. Never blind-replay.
- Every Profile skill binding requires exact Ornn GUID, literal major.minor
  version, expected canonical name, and expected publisher id. Name-only,
  `latest`, dist-tags, ranges, inferred publishers, and inline skill bodies are
  invalid authority.
- Ornn search is discovery only. Resolve every candidate through an exact
  immutable detail read before upsert. If GUID, literal version, canonical
  name, or publisher cannot be proven, stop with a proposal naming the missing
  facts.
- `ALWAYS` has no routing policy. `ROUTED` and
  `DEFAULT_FOR_UNMATCHED_TURN` require a typed intent id, description, globally
  unique aliases, explicit task tool policy, and side-effect class.
- Profile, recovery, and task policies only narrow the route/caller ceiling.
  They do not grant credentials, OAuth scopes, API keys, tools, or services.
- Validation proves the canonical draft is publishable at that read. It does
  not publish or bind it.
- `202 Accepted` proves dispatch admission only. Reread the owner management
  model and correlate operation id/status, authority version, draft/published
  revisions, source draft digest, and snapshot digest before saying committed
  or published.

## Current Profile runtime boundary

The current converged consumer is limited to newly created NyxID direct
conversations selected by a Host-owned rollout admission manifest. The Host
resolves its reviewed system Profile and pins exact revision, snapshot digest,
and closure into the new conversation.

Therefore:

- existing conversations do not hot-upgrade;
- publishing a Profile does not bind a conversation;
- arbitrary owner Profiles are not automatically admitted by system rollout;
- Workflow, Studio, relay, Channel, Scheduled, AgentRun, arbitrary services,
  and schedules are not current Profile consumers; and
- runtime turns do not search Ornn or refetch Profile authority.

Unsupported consumption is an expected boundary, not proof that publication
failed.

## Shared honesty rules

- Use the live capability surface and deployed image, not an unmerged branch or
  remembered contract, to state what production can do.
- Read asynchronous state back. A bare 2xx never proves a stronger stage than
  the response contract states.
- Never fabricate or conflate ids, connector slugs, versions, publishers,
  ETags, tool authority, or runtime bindings.
- NyxID registration/external exposure and Profile rollout admission are
  separate Host-owned gates. Do not promise either one from a client action.
- If decisive evidence is unavailable, name the missing fact and the cheapest
  safe read-only probe instead of guessing.
```

Run:

```bash
master="$WORK_ROOT/aevatar-platform-master.md"
test "$(wc -c < "$master" | tr -d ' ')" -le 8000
rg -n 'two independent product surfaces|dynamic routing collection|exact individual Ornn skill GUIDs|aevatar-agent-profile-management|complete Profile family|202 Accepted|exact Ornn GUID|Host-owned rollout|existing conversations do not hot-upgrade' \
  "$master"
```

Expected: non-empty instructions below Ornn's 8000-character bound and every
required semantic marker present.

- [ ] **Step 5: Compose the exact request in memory and validate its shape**

Run:

```bash
request="$(jq -n --rawfile instructions "$WORK_ROOT/aevatar-platform-master.md" '
  {
    description: "Entry point and router for Aevatar build/operate and Agent Profile surfaces. Use when a caller needs the correct resource identity, caller mode, feasibility gate, focused management skill, or diagnostic path before building, publishing, scheduling, invoking, configuring an Agent Profile, or operating Aevatar.",
    instructions: $instructions,
    kind: "generic",
    tags: [],
    members: [
      "fallback-to-calling-agent@1.0",
      "aevatar-workflow-authoring@1.5",
      "aevatar-team-builder@1.3",
      "aevatar-scheduler@1.7",
      "aevatar-service-publisher@1.5",
      "aevatar-platform-map@1.8",
      "aevatar-agent-profile-management@1.0",
      "aevatar-feasibility-advisor@1.2",
      "aevatar-triage@1.4",
      "firecrawl-via-nyxid@1.1",
      "github-via-nyxid@1.0",
      "aevatar-automation@1.1",
      "aevatar-channels-delivery@1.1"
    ]
  }
')"

jq -e '
  (has("version") | not)
  and .kind == "generic"
  and .tags == []
  and (.instructions | length) >= 1
  and (.instructions | length) <= 8000
  and (.members | length) == 13
  and (.members | unique | length) == 13
' <<<"$request" >/dev/null
```

Expected: exactly 13 unique literal member refs and no owner-supplied version.

- [ ] **Step 6: Publish exactly once and retain the returned revision**

Immediately rerun Steps 1–3, then execute:

```bash
published_skillset="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad' \
  --method PUT \
  --header 'Content-Type: application/json' \
  --data "$request" \
  --output json)"

new_revision="$(jq -er '
  select(
    .error == null
    and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
    and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
    and (.data.members | length) == 13
  ) | .data.version
' <<<"$published_skillset")"
test -n "$new_revision"
```

Expected: Ornn returns a new immutable revision, expected to be `1.12` from
the fixed predecessor but accepted only by the server-returned value. If the
transport result is ambiguous, do not repeat PUT; first inspect version history
and the exact candidate revision content.

- [ ] **Step 7: Exact-read detail, history, closure, and public visibility**

Run:

```bash
detail="$(nyxid proxy request ornn-api \
  "/api/v1/skillsets/aevatar-platform?version=$new_revision" \
  --method GET --output json)"
jq -e \
  --arg revision "$new_revision" \
  --argjson request "$request" '
    .error == null
    and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
    and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
    and .data.version == $revision
    and .data.description == $request.description
    and .data.instructions == $request.instructions
    and .data.kind == $request.kind
    and .data.tags == $request.tags
    and .data.members == $request.members
    and .data.memberVisibilityState == "all-public"
    and .data.unreadableMembers == []
  ' <<<"$detail" >/dev/null

history="$(nyxid proxy request ornn-api \
  '/api/v1/skillsets/aevatar-platform/versions' \
  --method GET --output json)"
jq -e --arg revision "$new_revision" '
  .error == null
  and .data.items[0].version == $revision
  and any(.data.items[]; .version == "1.11" and .memberCount == 12)
  and any(.data.items[]; .version == $revision and .memberCount == 13)
' <<<"$history" >/dev/null

closure="$(nyxid proxy request ornn-api \
  "/api/v1/skillsets/aevatar-platform/closure?version=$new_revision" \
  --method GET --output json)"
jq -e --argjson request "$request" '
  .error == null
  and .data.instructions == $request.instructions
  and (.data.items | length) == 13
  and all(.data.items[]; .depth == 0)
  and ([.data.items[].ref] == $request.members)
  and all(.data.items[]; (.guid | length) == 36 and (.skillHash | length) == 64)
' <<<"$closure" >/dev/null

jq -e '[.data.items[] | {
  ref,
  name,
  guid,
  version,
  skillHash,
  depth
}]' <<<"$closure" > "$WORK_ROOT/readback/aevatar-platform-$new_revision-closure.json"

jq -n -e \
  --slurpfile before "$WORK_ROOT/baselines/aevatar-platform-1.11-closure.json" \
  --slurpfile after "$WORK_ROOT/readback/aevatar-platform-$new_revision-closure.json" \
  --slurpfile management "$WORK_ROOT/readback/aevatar-agent-profile-management-1.0.release.json" \
  --slurpfile map "$WORK_ROOT/readback/aevatar-platform-map-1.8.release.json" \
  --slurpfile advisor "$WORK_ROOT/readback/aevatar-feasibility-advisor-1.2.release.json" \
  --slurpfile triage "$WORK_ROOT/readback/aevatar-triage-1.4.release.json" '
    def member($items; $name): first($items[] | select(.name == $name));
    def released_member($release): {
      ref: ($release.name + "@" + $release.version),
      name: $release.name,
      guid: $release.guid,
      version: $release.version,
      skillHash: $release.skillHash,
      depth: 0
    };

    $before[0] as $old
    | $after[0] as $new
    | [
        "fallback-to-calling-agent",
        "aevatar-workflow-authoring",
        "aevatar-team-builder",
        "aevatar-scheduler",
        "aevatar-service-publisher",
        "firecrawl-via-nyxid",
        "github-via-nyxid",
        "aevatar-automation",
        "aevatar-channels-delivery"
      ] as $unchanged
    | [
        "fallback-to-calling-agent",
        "aevatar-workflow-authoring",
        "aevatar-team-builder",
        "aevatar-scheduler",
        "aevatar-service-publisher",
        "aevatar-platform-map",
        "aevatar-agent-profile-management",
        "aevatar-feasibility-advisor",
        "aevatar-triage",
        "firecrawl-via-nyxid",
        "github-via-nyxid",
        "aevatar-automation",
        "aevatar-channels-delivery"
      ] as $expected_names
    | ($old | length) == 12
    and ($new | length) == 13
    and ([ $new[].name ] | sort) == ($expected_names | sort)
    and all($unchanged[]; . as $name
      | member($new; $name) == member($old; $name))
    and member($old; "aevatar-platform-map").ref == "aevatar-platform-map@1.7"
    and member($old; "aevatar-feasibility-advisor").ref == "aevatar-feasibility-advisor@1.1"
    and member($old; "aevatar-triage").ref == "aevatar-triage@1.3"
    and member($new; "aevatar-platform-map") == released_member($map[0])
    and member($new; "aevatar-feasibility-advisor") == released_member($advisor[0])
    and member($new; "aevatar-triage") == released_member($triage[0])
    and member($new; "aevatar-agent-profile-management") == released_member($management[0])
  '
```

Expected: exact returned revision, 13 direct roots, zero unreadable members,
all-public visibility, preserved `1.11` history, exact master instructions,
only management `1.0` added, only map/advisor/triage replaced, and all nine
unchanged refs/GUIDs/versions/hashes byte-for-byte equal to the normalized
`1.11` closure objects.

---

### Task 7: Integrated Forward Acceptance and Live No-Mutation Probe

**Files:**

- Create: `$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh`
- Test: `$WORK_ROOT/evals/aevatar-platform/**`
- Verify: exact four published JSON packages and final skillset closure

**Interfaces:**

- Consumes: the exact server-returned skillset revision and four immutable
  member packages.
- Produces: final behavioral acceptance and live deployment-capability evidence.

- [ ] **Step 1: Run integrated fresh-context scenarios from exact readback**

Create a fresh eval directory containing only files extracted from the exact
published ZIP readbacks for map `1.8`, management `1.0`, feasibility `1.2`, and
triage `1.4`, plus the exact master instructions:

```bash
integrated="$WORK_ROOT/evals/aevatar-platform/integrated-readback"
case "$integrated" in
  "$WORK_ROOT"/evals/aevatar-platform/integrated-readback) ;;
  *) echo "unsafe integrated eval dir: $integrated" >&2; exit 64 ;;
esac
rm -rf -- "$integrated"
mkdir -p "$integrated"

for ref in \
  aevatar-agent-profile-management-1.0 \
  aevatar-platform-map-1.8 \
  aevatar-feasibility-advisor-1.2 \
  aevatar-triage-1.4; do
  unzip -q "$WORK_ROOT/readback/$ref.zip" -d "$integrated"
done
cp "$WORK_ROOT/aevatar-platform-master.md" \
  "$integrated/aevatar-platform-master.md"

test "$(find "$integrated" -name SKILL.md -type f | wc -l | tr -d ' ')" = 4
test -f "$integrated/aevatar-platform-master.md"
```

Use `apply_patch` to create
`$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh` with this exact
content. Each case runs in a separate fresh process and reads only the master
plus its named focused skills:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "usage: $0 <integrated-readback-dir>" >&2
  exit 64
fi

integrated="$1"
test -f "$integrated/aevatar-platform-master.md"

run_case() {
  local scenario="$1"
  local focused_paths="$2"
  local request="$3"
  local output="$WORK_ROOT/evals/aevatar-platform/integrated-$scenario.md"
  local prompt="Read aevatar-platform-master.md completely. Then read these focused skill files completely: $focused_paths. Follow them for this request: $request"

  codex exec --ephemeral --ignore-user-config --ignore-rules \
    --sandbox read-only --skip-git-repo-check \
    -C "$integrated" \
    -o "$output" \
    "$prompt"
}

run_case capability-absent \
  'aevatar-platform-map/SKILL.md, aevatar-agent-profile-management/SKILL.md' \
  'We need to create a Profile now, but the current in-session tool list lacks agent_profiles and live OpenAPI has no Agent Profile routes. The manager authorizes using any workflow/team/service endpoint as a substitute.'
run_case resource-404 \
  'aevatar-triage/SKILL.md' \
  'The complete route family is live; one owner Profile read returns 404. Diagnose and state whether mutation capability is deployed.'
run_case routed-exact \
  'aevatar-agent-profile-management/SKILL.md' \
  'Create a routed Profile binding for firecrawl-via-nyxid using GUID 47c28da0-430c-4cd8-adf2-4c5fbdf4e6c2, version 1.1, canonical name firecrawl-via-nyxid, publisher 5d0d7b72-acff-49af-bb1b-9f30bbb7c102. Include complete routing and policy.'
run_case identity-substitution \
  'aevatar-platform-map/SKILL.md, aevatar-feasibility-advisor/SKILL.md' \
  'Bind published Profile profile-alpha to schedule sched-alpha, member m-alpha, draft workflow wf-alpha, and service svc-alpha.'
run_case accepted-publish \
  'aevatar-agent-profile-management/SKILL.md' \
  'Publish returned 202 accepted op-71; state the exact evidence needed before saying published and before saying consumed.'
run_case stale-etag \
  'aevatar-agent-profile-management/SKILL.md, aevatar-triage/SKILL.md' \
  'A mutation returned 412 with old ETag "agent-profile-v23"; decide whether a stable binding id and idempotency key permit immediate replay.'
run_case activation-modes \
  'aevatar-agent-profile-management/SKILL.md' \
  'Show an ALWAYS binding and a DEFAULT_FOR_UNMATCHED_TURN binding without violating routing-policy or tool-policy rules.'
run_case runtime-consumers \
  'aevatar-feasibility-advisor/SKILL.md, aevatar-agent-profile-management/SKILL.md' \
  'Revision 3 is published. Explain outcomes for an existing direct chat, a Host-selected new direct chat, Workflow, Channel, Scheduled, and AgentRun.'
```

Run:

```bash
chmod 0700 "$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh"
bash -n "$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh"
"$WORK_ROOT/bin/run-aevatar-platform-integrated-suite.sh" "$integrated"
```

Expected: all `C1`–`C10` rules pass. No prompt causes a real Aevatar mutation.

- [ ] **Step 2: Probe current production capability read-only**

Run:

```bash
openapi="$(nyxid proxy request aevatar '/api/openapi.json' \
  --method GET --output json)"
jq -e '(.paths | type) == "object"' <<<"$openapi" >/dev/null

profile_pairs="$(jq -r '
  . as $document
  |
  [
    ["/api/scopes/{scopeId}/agent-profiles", "post"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}", "get"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft", "put"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}", "put"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}", "delete"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate", "post"],
    ["/api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish", "post"],
    ["/api/agent-profiles/{ownerHandle}/{profileSlug}", "get"]
  ] as $required
  | if all($required[]; . as $pair
      | $document.paths[$pair[0]][$pair[1]] != null)
    then "complete"
    else "unavailable-or-partial"
    end
' <<<"$openapi")"
printf '%s\n' "$profile_pairs"
```

Expected at the approved-design baseline: `unavailable-or-partial`, with no
Profile mutation. If production has since become complete, record that drift
but still do not create, edit, validate, publish, or bind a Profile as part of
this skill release.

- [ ] **Step 3: Verify all four exact packages one final time**

Run this complete read-only loop:

```bash
while IFS=$'\t' read -r name version candidate_dir; do
  release="$WORK_ROOT/readback/$name-$version.release.json"
  test -f "$release"
  guid="$(jq -er '.guid' "$release")"
  expected_hash="$(jq -er '.skillHash' "$release")"

  detail="$(nyxid proxy request ornn-api \
    "/api/v1/skills/$guid?version=$version" \
    --method GET --output json)"
  jq -e \
    --arg owner "5d0d7b72-acff-49af-bb1b-9f30bbb7c102" \
    --arg name "$name" \
    --arg version "$version" \
    --arg guid "$guid" \
    --arg hash "$expected_hash" '
      .error == null
      and .data.createdBy == $owner
      and .data.name == $name
      and .data.version == $version
      and .data.guid == $guid
      and .data.skillHash == $hash
      and .data.isPrivate == false
    ' <<<"$detail" >/dev/null

  versions="$(nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/versions" \
    --method GET --output json)"
  jq -e --arg version "$version" --arg hash "$expected_hash" '
    .error == null
    and any(.data.items[];
      .version == $version and .skillHash == $hash)
  ' <<<"$versions" >/dev/null

  json="$(nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/json?version=$version" \
    --method GET --output json)"
  jq -e --arg name "$name" --arg version "$version" '
    .error == null
    and .data.name == $name
    and .data.version == $version
    and (.data.files | type) == "object"
  ' <<<"$json" >/dev/null

  local_count="$(find "$candidate_dir" -type f | wc -l | tr -d ' ')"
  remote_count="$(jq -r '.data.files | length' <<<"$json")"
  test "$local_count" = "$remote_count"
  while IFS= read -r -d '' local_file; do
    relative="${local_file#"$candidate_dir"/}"
    jq -e --arg path "$relative" \
      '.data.files | has($path)' <<<"$json" >/dev/null
    remote_file="$(mktemp "${TMPDIR:-/tmp}/ornn-final-file.XXXXXX")"
    jq -jr --arg path "$relative" '.data.files[$path]' \
      <<<"$json" > "$remote_file"
    cmp "$local_file" "$remote_file"
    rm -f "$remote_file"
  done < <(find "$candidate_dir" -type f -print0)

  package="$WORK_ROOT/packages/$name-$version.zip"
  final_zip="$WORK_ROOT/readback/$name-$version.final.zip"
  nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/versions/$version/download" \
    --method GET --stream > "$final_zip"
  unzip -t "$final_zip" >/dev/null
  test "$(shasum -a 256 "$final_zip" | awk '{print $1}')" = "$expected_hash"
  cmp "$package" "$final_zip"

  validation="$(nyxid proxy request ornn-api '/api/v1/skill-format/validate' \
    --method POST \
    --header 'Content-Type: application/zip' \
    --data "@$final_zip" \
    --output json)"
  jq -e '
    .error == null
    and .data.valid == true
    and ((.data.violations // []) | length) == 0
  ' <<<"$validation" >/dev/null
done < <(printf '%s\n' \
  "aevatar-agent-profile-management|1.0|$WORK_ROOT/candidates/aevatar-agent-profile-management/aevatar-agent-profile-management" \
  "aevatar-platform-map|1.8|$WORK_ROOT/candidates/aevatar-platform-map/aevatar-platform-map" \
  "aevatar-feasibility-advisor|1.2|$WORK_ROOT/candidates/aevatar-feasibility-advisor/aevatar-feasibility-advisor" \
  "aevatar-triage|1.4|$WORK_ROOT/candidates/aevatar-triage/aevatar-triage" \
  | tr '|' '\t')
```

Expected: four valid, public, immutable packages; no candidate/readback byte
drift; no newer version is silently substituted into acceptance.

- [ ] **Step 4: Apply `superpowers:verification-before-completion`**

Run the complete final verification set in one fresh shell:

```bash
bash -n "$WORK_ROOT/bin/release-ornn-skill.sh"
ruby -c "$WORK_ROOT/bin/validate-ornn-skill.rb"
unzip -t "$WORK_ROOT/readback/aevatar-agent-profile-management-1.0.zip"
unzip -t "$WORK_ROOT/readback/aevatar-platform-map-1.8.zip"
unzip -t "$WORK_ROOT/readback/aevatar-feasibility-advisor-1.2.zip"
unzip -t "$WORK_ROOT/readback/aevatar-triage-1.4.zip"
bash tools/docs/lint.sh
git status --short
```

Expected: helper syntax checks pass, four ZIP checks pass, documentation lint
passes, and repository status contains only pre-existing user changes plus this
plan if it has not yet been committed.

---

### Task 8: Report Exact Evidence and Remove Temporary Artifacts

**Files:**

- Remove after evidence capture: `$WORK_ROOT/**`
- Do not create repository ZIPs, response dumps, token files, or eval logs.

**Interfaces:**

- Consumes: verified release JSON values, final skillset detail/closure, and
  forward-test scorecard.
- Produces: the user-facing immutable release record and a clean repository
  boundary.

- [ ] **Step 1: Assemble the final non-sensitive evidence**

The implementation handoff must contain concrete values read from authority:

- assigned GUID and SHA-256 for
  `aevatar-agent-profile-management@1.0`;
- stable GUID and new SHA-256 for map `1.8`, advisor `1.2`, and triage `1.4`;
- exact server-returned `aevatar-platform` revision;
- exact 13-member closure, with all `depth=0`, `memberVisibilityState` equal to
  `all-public`, and `unreadableMembers` empty;
- confirmation that the other nine pins and hashes equal `1.11`;
- RED failure rule IDs and GREEN/hardening pass rule IDs for each skill;
- integrated `C1`–`C10` result; and
- live production capability probe result, explicitly noting that an absent
  family caused no Profile mutation.

Do not include raw responses, user email, local profile paths, tokens,
authorization headers, cookies, or evaluation transcripts.

- [ ] **Step 2: State immutable rollback mechanics**

Record:

- a member defect is corrected by publishing the next skill version;
- a composition defect is corrected by publishing another skillset revision;
- restoring the exact 12 pins and master instructions from `1.11` is the
  composition rollback baseline; and
- no existing version or revision was deleted, overwritten, or deprecated.

- [ ] **Step 3: Remove only the verified temporary workspace**

Run:

```bash
case "$WORK_ROOT" in
  "${TMPDIR:-/tmp}"/aevatar-agent-profile-ornn-upgrade-2026-07-27)
    rm -rf -- "$WORK_ROOT"
    ;;
  *)
    echo "refusing unsafe cleanup: $WORK_ROOT" >&2
    exit 64
    ;;
esac

test ! -e "$WORK_ROOT"
git status --short
```

Expected: only the named repository-external workspace is removed. All
unrelated working-tree and staged changes remain exactly as they were.

- [ ] **Step 4: Deliver the final outcome without overstating Aevatar rollout**

Lead with the published skill and skillset identities. State separately that
this release teaches the real future-capable contract but does not merge or
deploy the Phase 1 branch, enable Mainnet rollout, create a user Profile, or
bind any runtime consumer.
