# Aevatar Platform Tool Contract Skill Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Audit all 15 skills in the live `aevatar-platform` closure, publish corrected immutable versions for every skill whose tool guidance is stale, and publish and verify one rebased `aevatar-platform` skillset revision through the `nyxid` CLI.

**Architecture:** Treat refreshed merged Aevatar source as the tool-contract authority and exact Ornn JSON/ZIP readbacks as the skill-content authority. Work outside all repositories in one release directory, execute a RED-GREEN-validate-publish-readback cycle for one skill at a time, and update the skillset only after every changed exact version is independently proven. Preserve concurrent registry changes by rebasing on the latest immutable versions immediately before mutation.

**Tech Stack:** Git, NyxID CLI proxy transport, Ornn `/api/v1` APIs, ZIP packages, Markdown/YAML, `jq`, `shasum`, `rg`, shell checks, and fresh-context Codex CLI evaluations.

## Global Constraints

- Follow `docs/superpowers/specs/2026-07-28-aevatar-platform-tool-contract-skills-design.md` exactly.
- Do not modify Aevatar, NyxID, Ornn, chrono-sandbox, or downstream product code.
- Use `${TMPDIR:-/tmp}/aevatar-platform-tool-contract-skills-2026-07-28` outside every repository.
- Use `apply_patch` for authored text; use `unzip`, `cp`, and `zip` only for mechanical packages.
- Never print, copy, persist in the repository, or transfer between profiles a bearer, refresh token, API key, Agent Key, Codex credential, Vault reference, or delegation token.
- A CLI exit code is not upstream success. Require `error == null` and exact GUID, owner, name, and version from every JSON response.
- A successful `git fetch origin feature/integrate --prune` is mandatory before authoring and again before the skillset mutation. Stop on failure.
- Record the exact refreshed source revision and diff public tool contracts from baseline `aba74805c6b40f3848a554b85e4192e7c06abfa2`.
- Exclude unmerged worktrees and local experiments.
- Download and hash-verify all 15 exact live closure packages before deciding impact.
- Complete RED, edit, GREEN, validation, publication, JSON/ZIP readback, and SHA-256 comparison for one skill before editing the next.
- Preserve the exact version of a skill whose published package passes its applicable scenarios.
- Publish skills by stable GUID and exact increasing version. Publish the skillset by GUID and omit `version`.
- Never use `skip_validation=true`, `latest` dependencies, version ranges, name-only dependencies, or guessed IDs.
- Rebase on any concurrent skill or skillset version before publishing.
- Keep `memberId`, `workflowId`, `publishedServiceId`, UserService ID, operation ID, schedule ID, Agent Key ID, and Profile ID distinct.
- Keep raw proxy, interactive operation, and compiled admitted-operation call shapes distinct.
- Normal managed `codex_exec` remains credential-read-only; explicit preparation and authoritative readiness precede canaries.
- Do not claim publication until exact registry readback and fresh verification pass.

---

### Task 1: Refresh Authorities and Establish the Release Workspace

**Files:**
- Create: `$WORK_ROOT/source-revision.txt`
- Create: `$WORK_ROOT/skillset-current.json`
- Create: `$WORK_ROOT/skillset-current-closure.json`
- Create: `$WORK_ROOT/skillset-versions.json`
- Create: `$WORK_ROOT/owners.tsv`

**Interfaces:**
- Consumes: Git `origin/feature/integrate`, default NyxID profile, skillset GUID `248b99d6-36ff-4d41-bb45-baa25c6a9cad`.
- Produces: exact source revision, latest 15-member closure, and owner matrix.

- [ ] **Step 1: Create the external workspace and confirm the repository is clean**

```bash
export WORK_ROOT="${TMPDIR:-/tmp}/aevatar-platform-tool-contract-skills-2026-07-28"
rm -rf "$WORK_ROOT"
mkdir -p "$WORK_ROOT"/{baselines,candidates,packages,readback,evals,inventory,bin}
case "$WORK_ROOT" in "$PWD"/*) exit 1 ;; esac
git status --short --branch
```

Expected: the documentation worktree is clean and `WORK_ROOT` is outside it.

- [ ] **Step 2: Refresh and record source authority**

```bash
git -c http.version=HTTP/1.1 fetch origin feature/integrate --prune
source_revision="$(git rev-parse origin/feature/integrate)"
git merge-base --is-ancestor aba74805c6b40f3848a554b85e4192e7c06abfa2 "$source_revision"
git show -s --format='%H %cI %s' "$source_revision" | tee "$WORK_ROOT/source-revision.txt"
git diff --name-status \
  aba74805c6b40f3848a554b85e4192e7c06abfa2.."$source_revision" \
  -- 'src/**/*Tool*.cs' 'agents/**/*Tool*.cs' 'src/**/*.proto' \
  > "$WORK_ROOT/inventory/source-tool-files.tsv"
```

Expected: fetch exits 0, baseline is an ancestor, and the exact revision is recorded. Fetch failure stops the release.

- [ ] **Step 3: Assert the platform owner and permissions without printing credentials**

```bash
identity="$(nyxid whoami --output json)"
jq -e '.id == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"' <<<"$identity" >/dev/null
me="$(nyxid proxy request ornn-api '/api/v1/me' --method GET --output json)"
jq -e '.error == null
  and (.data.permissions | index("ornn:skill:read")) != null
  and (.data.permissions | index("ornn:skill:update")) != null' <<<"$me" >/dev/null
```

Expected: both assertions exit 0.

- [ ] **Step 4: Read and validate the latest skillset surfaces**

```bash
nyxid proxy request ornn-api '/api/v1/skillsets/aevatar-platform' \
  --method GET --output json > "$WORK_ROOT/skillset-current.json"
nyxid proxy request ornn-api '/api/v1/skillsets/aevatar-platform/closure' \
  --method GET --output json > "$WORK_ROOT/skillset-current-closure.json"
nyxid proxy request ornn-api '/api/v1/skillsets/aevatar-platform/versions' \
  --method GET --output json > "$WORK_ROOT/skillset-versions.json"
jq -e '.error == null
  and .data.guid == "248b99d6-36ff-4d41-bb45-baa25c6a9cad"
  and .data.createdBy == "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
  and .data.version == .data.latestVersion
  and .data.memberVisibilityState == "all-public"
  and (.data.members | length) == 15' "$WORK_ROOT/skillset-current.json"
jq -e '.error == null and (.data.items | length) == 15
  and ([.data.items[].name] | unique | length) == 15
  and all(.data.items[]; (.skillHash | length) == 64)' \
  "$WORK_ROOT/skillset-current-closure.json"
```

Expected: latest all-public 15-member closure with unique names and hashes.

- [ ] **Step 5: Read every skill GUID and owner sequentially**

```bash
: > "$WORK_ROOT/owners.tsv"
while IFS=$'\t' read -r name version; do
  detail="$(nyxid proxy request ornn-api "/api/v1/skills/$name" --method GET --output json)"
  jq -e --arg name "$name" '.error == null and .data.name == $name
    and (.data.guid | length) > 0 and (.data.createdBy | length) > 0' \
    <<<"$detail" >/dev/null
  jq -r --arg version "$version" \
    '[.data.name,$version,.data.guid,.data.createdBy] | @tsv' \
    <<<"$detail" >> "$WORK_ROOT/owners.tsv"
done < <(jq -r '.data.items[] | [.name,.version] | @tsv' \
  "$WORK_ROOT/skillset-current-closure.json")
test "$(wc -l < "$WORK_ROOT/owners.tsv" | tr -d ' ')" = 15
```

Expected: 15 exact `name/version/GUID/owner` rows.

---

### Task 2: Download and Audit All 15 Immutable Skills

**Files:**
- Create: `$WORK_ROOT/baselines/<skill>/<version>.zip`
- Create: `$WORK_ROOT/baselines/<skill>/<version>/<skill>/**`
- Create: `$WORK_ROOT/inventory/tool-mentions.tsv`
- Create: `$WORK_ROOT/inventory/impact.tsv`
- Create: `$WORK_ROOT/evals/audit/**`

**Interfaces:**
- Consumes: Task 1 closure and owners.
- Produces: byte-verified baselines and one impact decision per member.

- [ ] **Step 1: Ensure every owner profile is exact**

The default profile owns platform skills. Owner `2db990b5-29ea-4a32-acf5-0008420afa1f` uses `--profile codex-skill-owner`. If that profile is absent, run `nyxid login --profile codex-skill-owner --base-url https://nyx-api.chrono-ai.fun`, then require exact owner ID with `nyxid whoami --profile codex-skill-owner --output json`. Never reuse the default token for another owner.

- [ ] **Step 2: Download each exact ZIP and verify the closure hash**

```bash
while IFS=$'\t' read -r name version guid owner; do
  profile_args=()
  [[ "$owner" == "2db990b5-29ea-4a32-acf5-0008420afa1f" ]] && profile_args=(--profile codex-skill-owner)
  mkdir -p "$WORK_ROOT/baselines/$name/$version"
  zip_path="$WORK_ROOT/baselines/$name/$version.zip"
  nyxid proxy request ornn-api "/api/v1/skills/$name/versions/$version/download" \
    "${profile_args[@]}" --method GET --stream > "$zip_path"
  unzip -t "$zip_path" >/dev/null
  actual="$(shasum -a 256 "$zip_path" | awk '{print $1}')"
  expected="$(jq -er --arg name "$name" '.data.items[] | select(.name == $name) | .skillHash' \
    "$WORK_ROOT/skillset-current-closure.json")"
  test "$actual" = "$expected"
  unzip -q "$zip_path" -d "$WORK_ROOT/baselines/$name/$version"
  test -f "$WORK_ROOT/baselines/$name/$version/$name/SKILL.md"
done < "$WORK_ROOT/owners.tsv"
```

Expected: 15 valid hash-matching packages.

- [ ] **Step 3: Read exact JSON packages and compare every file**

For every owner row, call `/api/v1/skills/<name>/json?version=<version>` with the same profile, require exact name/version, compare file counts, and compare every local extracted file with `.data.files[path]` using `cmp`. Store JSON beside each ZIP.

Expected: all JSON file maps match extracted packages exactly.

- [ ] **Step 4: Build the tool-mention inventory**

```bash
: > "$WORK_ROOT/inventory/tool-mentions.tsv"
while IFS=$'\t' read -r name version guid owner; do
  root="$WORK_ROOT/baselines/$name/$version/$name"
  rg -n --no-heading -i \
    'nyxid_proxy|nyxid_service_|nyxid_require_service|list_external_workflow_capabilities|inspect_external_workflow_capability_readiness|aevatar_invoke_member|aevatar_invoke_team|aevatar_start_workflow|aevatar_observe_run|use_skill|codex_exec|managed-codex' \
    "$root" | while IFS= read -r hit; do
      printf '%s\t%s\t%s\n' "$name" "$version" "$hit"
    done >> "$WORK_ROOT/inventory/tool-mentions.tsv" || true
done < "$WORK_ROOT/owners.tsv"
```

Expected: an exact package/file/line mapping for all relevant calls.

- [ ] **Step 5: Run old-version RED and no-skill controls**

Run a fresh isolated Codex CLI process for each member. Save response and trace. Use these exact scenario groups:

- workflow authoring: admitted Lark operation, readiness selector, tool arguments;
- team/service: invoke `m-alpha` while `wf-alpha` and `svc-alpha` exist;
- platform map: route raw proxy, dynamic operation, admitted workflow, and member invocation;
- GitHub/Firecrawl: emit raw, dynamic, and workflow call shapes without mixing fields;
- channels: list sender services with `{}` and fetch an admitted Lark resource;
- Codex sample/setup/advisor/triage: explicit preparation, `execution_ready`, timeout hierarchy, canary, typed failure attribution;
- scheduler, automation, Agent Profile, fallback: retrieve current tool calls and lifecycle promises.

Mark a skill affected only when its exact old package emits a stale call, omits a required distinction, or teaches a false lifecycle promise. A correct old skill remains unchanged.

- [ ] **Step 6: Freeze the complete impact ledger**

Write `impact.tsv` with exact columns:

```text
name	baseline_version	status	failure_evidence	owned_contract	planned_version
```

Use `affected` or `unchanged`. Determine planned versions only after reading complete live history. Use the next minor for corrective/additive guidance and a major only when the canonical public contract is replaced. Require 15 unique rows.

---

### Task 3: Upgrade and Publish Each Affected Skill Independently

**Files:**
- Create: `$WORK_ROOT/candidates/<skill>/<skill>/**`
- Create: `$WORK_ROOT/packages/<skill>-<version>.zip`
- Create: `$WORK_ROOT/readback/<skill>-<version>.json`
- Create: `$WORK_ROOT/readback/<skill>-<version>.zip`
- Create: `$WORK_ROOT/evals/<skill>/**`
- Create: `$WORK_ROOT/inventory/published.tsv`

**Interfaces:**
- Consumes: one affected ledger row and its exact RED evidence.
- Produces: one immutable validated, hash-verified skill version per iteration.

- [ ] **Step 1: Recheck the live target before editing**

Read live detail and versions with the owner profile. Assert GUID/owner from `owners.tsv`. If latest differs from the ledger baseline, download/hash it, rerun RED, and update the ledger before continuing.

- [ ] **Step 2: Copy the exact baseline and make only RED-driven edits**

Copy its extracted root to `candidates/<skill>/<skill>`. Use `apply_patch` for `SKILL.md` and only required references/assets. Preserve unrelated content and dependencies. Apply only relevant recipes:

- raw proxy: exact `service_id + slug + path`;
- dynamic operation: enumerated `user_service_id` plus emitted operation fields;
- admitted workflow: operation values only, never forged route/proof fields;
- readiness: listed `selector + execution_mode`;
- channel inventory: `{}`;
- connected-service OpenAPI override: omission preserves, a non-empty
  `openapi_spec_url` sets, and an empty string clears the override;
- member invocation: `member_id + payload` with optional known non-chat endpoint;
- managed Codex: explicit preparation, authoritative readiness, then workflow timeout at least 360 seconds.

- [ ] **Step 3: Run static and candidate GREEN checks**

Inspect `git diff --no-index` from baseline to candidate. Scan for raw route fields in admitted calls, removed readiness `capability` bags, hand-written channel inventory IDs, identity equality, automatic managed credential repair, active-without-ready decisions, under-budget canaries, and secrets. Manually inspect every match. Repeat the exact RED prompt against the candidate and require the corrected exact tool/schema/lifecycle behavior.

- [ ] **Step 4: Package deterministically and validate with Ornn**

```bash
find "$candidate_root" -type f -exec touch -t 202607280000 {} +
(cd "$(dirname "$candidate_root")" && \
  find "$(basename "$candidate_root")" -type f -print | LC_ALL=C sort | \
  zip -X -q "$package" -@)
unzip -t "$package" >/dev/null
validation="$(nyxid proxy request ornn-api '/api/v1/skill-format/validate' \
  "${profile_args[@]}" --method POST --data "@$package" \
  --header 'Content-Type:application/zip' --output json)"
jq -e '.error == null and .data.valid == true
  and (.data.violations | length) == 0' <<<"$validation" >/dev/null
```

Expected: valid deterministic ZIP with zero violations.

- [ ] **Step 5: Publish exactly once by stable GUID**

Read history first. If the planned version exists, continue only when its
server hash equals the candidate hash. Otherwise publish the already validated
ZIP through the same immutable update contract verified by the prior release:

```bash
publish="$(nyxid proxy request ornn-api "/api/v1/skills/$guid" \
  "${profile_args[@]}" --method PUT --data "@$package" \
  --header 'Content-Type:application/zip' --output json)"
jq -e --arg name "$name" --arg version "$planned_version" \
  '.error == null and .data.name == $name and .data.version == $version' \
  <<<"$publish" >/dev/null
```

The exact version is declared inside the candidate package. Do not add
`skip_validation=true`. Assert exact GUID/name/version/owner from the response.
An ambiguous mutation is never retried until a versions readback proves whether
it committed.

- [ ] **Step 6: Verify JSON, ZIP, hash, and published behavior**

Download exact JSON and ZIP. Require `cmp` between uploaded and downloaded ZIP, equality of local/download/server SHA-256, and equality of every candidate file with `.data.files`. Append name/version/GUID/hash to `published.tsv` only after all pass. Repeat GREEN using only published readback files. A failure requires a corrected later immutable version.

- [ ] **Step 7: Repeat sequentially for all affected rows**

Do not begin another candidate before the current skill passes readback. At the end, require exactly one final successful `published.tsv` row per affected name and none for unchanged names.

---

### Task 4: Rebase and Publish the Skillset

**Files:**
- Create: `$WORK_ROOT/skillset-prepublish.json`
- Create: `$WORK_ROOT/skillset-publish.json`
- Create: `$WORK_ROOT/skillset-published-detail.json`
- Create: `$WORK_ROOT/skillset-published-closure.json`
- Create: `$WORK_ROOT/skillset-published-versions.json`
- Create: `$WORK_ROOT/evals/aevatar-platform/**`

**Interfaces:**
- Consumes: latest live skillset, unchanged refs, and final published changed refs.
- Produces: one system-assigned immutable skillset revision.

- [ ] **Step 1: Refresh source and registry immediately before mutation**

Repeat Task 1 fetch and registry reads. If source changed, redo contract delta and affected evaluations. If skillset advanced, rebase its description, tags, members, and instructions.

- [ ] **Step 2: Build exact members and complete rebased instructions**

Start from latest live members. Replace only names in `published.tsv`, preserving all other refs and order. Require 15 unique refs/names. Write `skillset-publish.json` with no version. Preserve current router rules and add only verified deltas: actual schema selection, the three NyxID modes, exact readiness selector, direct member invocation, empty-object channel inventory, and explicit managed Codex readiness. Require instructions length 1-8000 bytes.

- [ ] **Step 3: Resolve every proposed dependency closure**

Call `/api/v1/skills/<name>/closure?version=<version>` for each root. Combine nodes and require one version per name, valid hashes, readable public members, and no conflicts.

- [ ] **Step 4: Run integrated prepublish evaluation**

Using only proposed exact readbacks, require one answer that lists channel services, calls a dynamic operation, authors the admitted workflow call, checks exact readiness, invokes `m-alpha` without confusing `wf-alpha`/`svc-alpha`, prepares/runs managed Codex, and diagnoses route-field rejection plus timeout. Inspect full text and trace.

- [ ] **Step 5: Publish once and verify all registry surfaces**

Capture current version and expected next minor. Issue one `PUT /api/v1/skillsets/248b99d6-36ff-4d41-bb45-baa25c6a9cad` through `nyxid proxy request` with the complete JSON. Assert exact GUID/name/owner/system-assigned version. Read exact detail/history/closure and require all-public visibility, byte-equal instructions, exact members, unique conflict-free closure, changed hashes from `published.tsv`, unchanged refs from prepublish, and preservation of the prior revision.

- [ ] **Step 6: Forward-test only published skillset readbacks**

Independently download the final closure and repeat the integrated evaluation without candidate or product-source inputs.

---

### Task 5: Record Evidence and Verify the Repository

**Files:**
- Modify: `docs/superpowers/specs/2026-07-28-aevatar-platform-tool-contract-skills-design.md`
- Modify: `docs/superpowers/plans/2026-07-28-aevatar-platform-tool-contract-skills.md`

**Interfaces:**
- Consumes: exact final readbacks and evaluations.
- Produces: non-secret publication evidence and final handoff.

- [ ] **Step 1: Update design and plan from actual evidence**

Record source revision, affected/unchanged matrix, every published skill version/GUID/hash, final skillset version/visibility/closure, evaluation paths, and limitations. Mark only actually completed checkboxes. Include no secrets or raw identity envelopes.

- [ ] **Step 2: Run fresh verification**

```bash
bash tools/docs/lint.sh
git diff --check
git status --short
git diff -- \
  docs/superpowers/specs/2026-07-28-aevatar-platform-tool-contract-skills-design.md \
  docs/superpowers/plans/2026-07-28-aevatar-platform-tool-contract-skills.md
```

Expected: lint and diff checks pass; only the two evidence documents differ.

- [ ] **Step 3: Commit only final evidence**

```bash
git add \
  docs/superpowers/specs/2026-07-28-aevatar-platform-tool-contract-skills-design.md \
  docs/superpowers/plans/2026-07-28-aevatar-platform-tool-contract-skills.md
git diff --cached --check
git diff --cached --name-only
git commit -m "Document Ornn tool contract skill publication"
```

Expected: exactly the spec and plan are committed.

- [ ] **Step 4: Report the real published state**

Report final skillset revision, changed and unchanged versions, new invocation contracts, source refresh, validation/readback evidence, and any limitation. Do not imply a skill changed merely because it was audited.
