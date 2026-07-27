# Public Codex Exec Smoke Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a public Ornn skill named `verify-codex-exec` that runs one canonical managed `codex_exec` smoke test and reports an account-scoped verdict without false positives or credential disclosure.

**Architecture:** Keep the Ornn package to one `SKILL.md` declaring only the existing `codex_exec` tool. Develop and commit the package in an isolated Ornn worktree, validate it against the live Ornn format endpoint, publish it privately, deploy the focused direct-ingress NyxID authority propagation fix from `2026-07-27-direct-responses-nyxid-authority.md`, run one authenticated end-to-end Aevatar check, and only then switch the immutable package version to public and verify anonymous visibility.

**Tech Stack:** Ornn skill package format, Markdown/YAML frontmatter, NyxID CLI, Ornn `/api/v1` API, Aevatar `/v1/responses`, shell assertions with `jq`, Git worktrees.

## Global Constraints

- Keep Ornn package work separate from Aevatar runtime work. The only Aevatar prerequisite is the typed authority propagation fix in `docs/superpowers/plans/2026-07-27-direct-responses-nyxid-authority.md`; do not widen its scope or change production configuration.
- Use the canonical target `managed_sandbox`, workspace `empty_git`, prompt `Reply with exactly CODEX_EXEC_READY`, and `timeout_secs=180`.
- Invoke `codex_exec` exactly once per verification and never retry automatically.
- Declare the Ornn category as `tool-based` and the tool list as exactly `codex_exec`.
- Do not declare `output-type`; Ornn forbids it for `tool-based` packages.
- Bundle no scripts, workflows, references, assets, credentials, service identifiers, or environment variables.
- Treat the check as available only when the managed result has `status=succeeded`, `target=managed_sandbox`, `exit_code=0`, and trimmed output exactly `CODEX_EXEC_READY`.
- Preserve stable error codes and redacted diagnostic IDs on failure; never expose access tokens, agent keys, headers, or secret references.
- Upload privately, verify the immutable package and a live Aevatar run, then change permissions to public.
- Fail closed: if any validation, live execution, permission, anonymous search, or anonymous read gate fails, do not claim the skill is public and ready.

## File Map

- Create: `/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/SKILL.md`
  - Owns the complete public diagnostic behavior and Ornn metadata.
- Modify: `docs/superpowers/specs/2026-07-27-codex-exec-public-smoke-skill-design.md`
  - Records the live Ornn rule that `tool-based` packages must omit `output-type` and that the private live check precedes public promotion.
- Create: `docs/superpowers/plans/2026-07-27-codex-exec-public-smoke-skill.md`
  - Records the reproducible implementation, validation, publication, and rollback sequence.

---

### Task 1: Establish the Ornn source worktree and RED catalog baseline

**Files:**

- Create later: `/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/SKILL.md`
- Read: `/Users/eanzhao/Code/Ornn/CLAUDE.md`
- Read: `/Users/eanzhao/Code/Ornn/skills/ornn-agent-manual-cli/SKILL.md`

**Interfaces:**

- Consumes: current authenticated NyxID CLI session and Ornn service slug `ornn-api`.
- Produces: clean Ornn worktree based on `origin/develop` and recorded proof that the exact public skill does not exist before implementation.

- [ ] **Step 1: Fetch the authoritative Ornn base**

Run:

```bash
git -C /Users/eanzhao/Code/Ornn fetch origin develop
```

Expected: `origin/develop` fetch succeeds without changing the existing dirty Ornn checkout.

- [ ] **Step 2: Create the isolated Ornn worktree**

Run:

```bash
git -C /Users/eanzhao/Code/Ornn worktree add \
  /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec \
  -b feature/verify-codex-exec \
  origin/develop
```

Expected: the new worktree is on `feature/verify-codex-exec`, tracks the latest `origin/develop`, and `git status --short` is empty.

- [ ] **Step 3: Run the RED public-catalog assertion**

Run:

```bash
result="$(
  nyxid proxy request ornn-api \
    '/api/v1/skill-search?query=verify-codex-exec&mode=keyword&scope=public&page=1&pageSize=20' \
    --method GET \
    --output json
)"

jq -e '
  .error == null and
  ([.data.items[]? | select(.name == "verify-codex-exec")] | length) == 0
' <<<"$result"
```

Expected: exit `0`, proving no exact public package exists. This is the RED state: users cannot discover or load the requested skill.

- [ ] **Step 4: Fail closed on an authenticated name collision**

Run:

```bash
result="$(
  nyxid proxy request ornn-api \
    '/api/v1/skill-search?query=verify-codex-exec&mode=keyword&scope=mixed&page=1&pageSize=100' \
    --method GET \
    --output json
)"

jq -e '
  .error == null and
  ([.data.items[]? | select(.name == "verify-codex-exec")] | length) == 0
' <<<"$result"
```

Expected: exit `0`. If an exact private/shared/public package appears, stop before creating or overwriting anything and inspect ownership/version instead.

### Task 2: Create, validate, and commit the minimal Ornn skill

**Files:**

- Create: `/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/SKILL.md`
- Delete after scaffolding: `/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/agents/openai.yaml`
- Package: `/tmp/verify-codex-exec-1.0.zip`

**Interfaces:**

- Consumes: Ornn live format rules and the canonical Aevatar `codex_exec` request/result contract.
- Produces: a committed, live-Ornn-validated one-file package that makes exactly one managed tool call and applies strict success semantics.

- [ ] **Step 1: Initialize the skill scaffold**

Run:

```bash
python /Users/eanzhao/.codex/skills/.system/skill-creator/scripts/init_skill.py \
  verify-codex-exec \
  --path /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills \
  --interface 'display_name=Verify Codex Exec' \
  --interface 'short_description=Check managed codex_exec access for this account' \
  --interface 'default_prompt=Use $verify-codex-exec to check whether managed codex_exec works for my account.'
```

Expected: the scaffold creates `SKILL.md` and `agents/openai.yaml`.

The Ornn package root allow-list does not permit `agents/`. Delete the generated `agents/openai.yaml` with `apply_patch`, then remove the empty directory:

```bash
rmdir /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/agents
```

- [ ] **Step 2: Replace the scaffold with the complete skill**

Write exactly:

````markdown
---
name: verify-codex-exec
description: "Use when a user asks whether managed codex_exec is available for their current account, requests a codex_exec readiness check, or wants to run the canonical managed Codex smoke test."
metadata:
  category: tool-based
  tool-list:
    - "codex_exec"
  tag:
    - "aevatar"
    - "codex"
    - "managed-sandbox"
    - "smoke-test"
    - "diagnostics"
version: "1.0"
---

# Verify Codex Exec

## Purpose

Prove whether the current authenticated account can complete a real managed
`codex_exec` run. Loading this skill, reading configuration, or observing a
healthy service is not proof.

## Required Check

1. Confirm `codex_exec` exists in the current tool set. If it is absent, do not
   call another tool or simulate success.
2. Invoke `codex_exec` exactly once with:

```json
{
  "target": {
    "kind": "managed_sandbox"
  },
  "workspace": {
    "kind": "empty_git"
  },
  "prompt": "Reply with exactly CODEX_EXEC_READY",
  "timeout_secs": 180
}
```

Do not alter the target, workspace, prompt, or timeout. Do not retry
automatically. Do not ask the user for a token, agent key, credential, service
ID, provider, model route, or sandbox setting.

## Verdict

Return `AVAILABLE` only when all of these are true:

- the tool call completed;
- its result is a JSON object with `status` equal to `succeeded`;
- `target` equals `managed_sandbox`;
- `exit_code` equals `0`;
- trimmed `output` equals exactly `CODEX_EXEC_READY`.

Start the final answer with `AVAILABLE`, then explain in the user's language
that managed `codex_exec` works for this account. Include `target` and
`elapsed_ms` when present.

For every other result, never claim success:

- start with `UNAVAILABLE` when the tool is absent, disabled, the user is
  ineligible, or credential readiness fails;
- start with `INCONCLUSIVE` for timeout, malformed result, execution failure,
  or unexpected output.

Include the stable `code` or `error_code` and `diagnostic_id` when present.
Explain the category briefly in the user's language. Do not print a raw
upstream response when those stable fields are available.

Never expose access tokens, agent keys, request headers, credentials, or secret
references.
````

- [ ] **Step 3: Run the local skill validator**

Run:

```bash
python /Users/eanzhao/.codex/skills/.system/skill-creator/scripts/quick_validate.py \
  /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec
```

Expected: validation succeeds.

- [ ] **Step 4: Assert the package and behavioral invariants**

Run:

```bash
skill=/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec

test "$(find "$skill" -mindepth 1 -maxdepth 1 -print | wc -l | tr -d ' ')" = "1"
test -f "$skill/SKILL.md"
test "$(rg -c '^    - "codex_exec"$' "$skill/SKILL.md")" = "1"
test "$(rg -c '"timeout_secs": 180' "$skill/SKILL.md")" = "1"
test "$(rg -c 'Reply with exactly CODEX_EXEC_READY' "$skill/SKILL.md")" = "1"
test "$(rg -c 'Invoke `codex_exec` exactly once' "$skill/SKILL.md")" = "1"
! rg -n 'output-type|runtime-env-var|agent key:|access token:' "$skill/SKILL.md"
```

Expected: every assertion exits `0`; only the final negative `rg` prints nothing.

- [ ] **Step 5: Build the ZIP with one root folder**

Run:

```bash
cd /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills
zip -X -q -r /tmp/verify-codex-exec-1.0.zip verify-codex-exec
unzip -Z1 /tmp/verify-codex-exec-1.0.zip
```

Expected output:

```text
verify-codex-exec/
verify-codex-exec/SKILL.md
```

- [ ] **Step 6: Validate the ZIP through the live Ornn endpoint**

Run:

```bash
result="$(
  nyxid proxy request ornn-api \
    '/api/v1/skill-format/validate' \
    --method POST \
    --data @/tmp/verify-codex-exec-1.0.zip \
    --header 'Content-Type:application/zip' \
    --output json
)"

jq -e '
  .error == null and
  .data.valid == true and
  ((.data.violations // []) | length) == 0
' <<<"$result"
```

Expected: exit `0`. On any violation, keep the skill private and return to Task 2.

- [ ] **Step 7: Verify source cleanliness**

Run:

```bash
git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec diff --check
git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec status --short
```

Expected: only `?? skills/verify-codex-exec/` appears; `diff --check` exits `0`.

- [ ] **Step 8: Commit the validated source**

Run:

```bash
git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec add \
  skills/verify-codex-exec/SKILL.md

git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec commit \
  -m 'feat(skills): add codex exec readiness check'
```

Expected: one commit containing only the public skill source.

### Task 3: Publish privately, read back exactly, and run the live GREEN check

**Files:**

- Read: `/tmp/verify-codex-exec-1.0.zip`
- No credentials or publish receipts are persisted to disk.

**Interfaces:**

- Consumes: authenticated NyxID session, validated ZIP, Aevatar model `chrono-llm/gpt-5.5`, and a deployed Aevatar build containing the direct-ingress typed NyxID authority propagation fix.
- Produces: private Ornn GUID/version/hash and one successful real `codex_exec` result before public exposure.

- [ ] **Step 1: Recheck that the exact name is still unused**

Run:

```bash
result="$(
  nyxid proxy request ornn-api \
    '/api/v1/skill-search?query=verify-codex-exec&mode=keyword&scope=mixed&page=1&pageSize=100' \
    --method GET \
    --output json
)"

jq -e '
  .error == null and
  ([.data.items[]? | select(.name == "verify-codex-exec")] | length) == 0
' <<<"$result"
```

Expected: exit `0`. Stop on collision.

- [ ] **Step 2: Upload the package privately**

Run:

```bash
publish="$(
  nyxid proxy request ornn-api \
    '/api/v1/skills' \
    --method POST \
    --data @/tmp/verify-codex-exec-1.0.zip \
    --header 'Content-Type:application/zip' \
    --output json
)"

jq -e '
  .error == null and
  .data.name == "verify-codex-exec" and
  .data.version == "1.0" and
  .data.isPrivate == true and
  (.data.guid | type == "string" and length > 0) and
  (.data.skillHash | type == "string" and length > 0)
' <<<"$publish"

jq '{guid: .data.guid, name: .data.name, version: .data.version, skillHash: .data.skillHash, isPrivate: .data.isPrivate}' <<<"$publish"
```

Expected: the first `jq` exits `0`; the second prints only non-secret package identity.

- [ ] **Step 3: Read back the exact private version**

Run:

```bash
detail="$(
  nyxid proxy request ornn-api \
    '/api/v1/skills/verify-codex-exec/json?version=1.0' \
    --method GET \
    --output json
)"

jq -e '
  .error == null and
  .data.name == "verify-codex-exec" and
  .data.version == "1.0" and
  .data.metadata.category == "tool-based" and
  .data.metadata.tools == [{"tool":"codex_exec","type":"mcp"}] and
  ((.data.files | keys) == ["SKILL.md"])
' <<<"$detail"
```

Expected: exit `0`. If Ornn serializes the tool declaration with equivalent field ordering, compare as objects rather than raw JSON text.

- [ ] **Step 4: Invoke the private skill through Aevatar**

Run:

```bash
response="$(
  nyxid proxy request aevatar \
    '/v1/responses' \
    --method POST \
    --header 'Content-Type:application/json' \
    --data '{
      "model": "chrono-llm/gpt-5.5",
      "input": "::verify-codex-exec",
      "max_output_tokens": 1200
    }' \
    --output json
)"

jq -e '
  .status == "completed" and
  ([.output[]? | select(.type == "function_call" and .name == "codex_exec")] | length) == 1 and
  (
    [
      .output[]?
      | select(.type == "function_call" and .name == "codex_exec")
      | (.arguments | fromjson)
      | select(
          .status == "succeeded" and
          .target == "managed_sandbox" and
          .exit_code == 0 and
          ((.output // "") | gsub("^\\s+|\\s+$"; "") == "CODEX_EXEC_READY")
        )
    ]
    | length
  ) == 1 and
  (
    [
      .output[]?
      | select(.type == "message")
      | .content[]?
      | select(.type == "output_text")
      | .text
    ]
    | join("\n")
    | startswith("AVAILABLE")
  )
' <<<"$response"
```

Expected: exit `0`. This proves the private package triggers exactly one real managed execution and applies the strict final verdict.

- [ ] **Step 5: Print only redacted live evidence**

Run:

```bash
jq '
  {
    response_id: .id,
    status: .status,
    verdict: (
      [
        .output[]?
        | select(.type == "message")
        | .content[]?
        | select(.type == "output_text")
        | .text
      ]
      | join("\n")
      | split("\n")[0]
    ),
    codex: (
      [
        .output[]?
        | select(.type == "function_call" and .name == "codex_exec")
        | (.arguments | fromjson)
        | {
            status,
            target,
            exit_code,
            diagnostic_id,
            elapsed_ms,
            output
          }
      ]
      | first
    )
  }
' <<<"$response"
```

Expected: output contains no bearer, agent key, request header, or secret reference.

### Task 4: Promote the verified package to public and prove anonymous visibility

**Files:**

- No local source changes.
- No credentials or permission responses are persisted to disk.

**Interfaces:**

- Consumes: the exact private `verify-codex-exec@1.0` that passed Task 3.
- Produces: public Ornn permissions plus anonymous exact search/read proof for the same GUID/version/hash.

- [ ] **Step 1: Resolve the immutable GUID and current hash**

Run:

```bash
detail="$(
  nyxid proxy request ornn-api \
    '/api/v1/skills/verify-codex-exec?version=1.0' \
    --method GET \
    --output json
)"

jq -e '
  .error == null and
  .data.name == "verify-codex-exec" and
  .data.version == "1.0" and
  .data.isPrivate == true
' <<<"$detail"

guid="$(jq -r '.data.guid' <<<"$detail")"
hash="$(jq -r '.data.skillHash' <<<"$detail")"
test -n "$guid"
test -n "$hash"
```

Expected: all commands exit `0`.

- [ ] **Step 2: Replace permissions with public visibility**

Run in the same shell as Step 1:

```bash
permissions="$(
  nyxid proxy request ornn-api \
    "/api/v1/skills/$guid/permissions" \
    --method PUT \
    --data '{"isPrivate":false,"sharedWithUsers":[],"sharedWithOrgs":[]}' \
    --output json
)"

jq -e '
  .error == null and
  .data.skill.guid == $guid and
  .data.skill.name == "verify-codex-exec" and
  .data.skill.isPrivate == false
' --arg guid "$guid" <<<"$permissions"
```

Expected: exit `0`.

- [ ] **Step 3: Verify anonymous public search**

Run:

```bash
public_search="$(
  nyxid public request ornn-api \
    '/api/v1/skill-search?query=verify-codex-exec&mode=keyword&scope=public&page=1&pageSize=20'
)"

jq -e '
  .error == null and
  (
    [
      .data.items[]?
      | select(
          .name == "verify-codex-exec" and
          .isPrivate == false and
          .latestVersion == "1.0"
        )
    ]
    | length
  ) == 1
' <<<"$public_search"
```

Expected: exit `0`.

- [ ] **Step 4: Verify anonymous exact package read**

Run in the same shell as Step 1:

```bash
public_json="$(
  nyxid public request ornn-api \
    "/api/v1/skills/$guid/json?version=1.0"
)"

jq -e '
  .error == null and
  .data.name == "verify-codex-exec" and
  .data.version == "1.0" and
  .data.metadata.category == "tool-based" and
  .data.metadata.tools == [{"tool":"codex_exec","type":"mcp"}] and
  ((.data.files | keys) == ["SKILL.md"])
' <<<"$public_json"

public_hash="$(jq -r '.data.skillHash // empty' <<<"$public_json")"
test -z "$public_hash" || test "$public_hash" = "$hash"
```

Expected: the public package is anonymously readable. If the JSON surface omits `skillHash`, the exact GUID/version and file checks remain mandatory.

### Task 5: Final verification and handoff

**Files:**

- Verify: `/Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec/SKILL.md`
- Verify: `docs/superpowers/specs/2026-07-27-codex-exec-public-smoke-skill-design.md`
- Verify: `docs/superpowers/plans/2026-07-27-codex-exec-public-smoke-skill.md`

**Interfaces:**

- Consumes: all local and live evidence from Tasks 1–4.
- Produces: final public skill identity, user invocation syntax, and a concise verification report.

- [ ] **Step 1: Re-run source validation**

Run:

```bash
python /Users/eanzhao/.codex/skills/.system/skill-creator/scripts/quick_validate.py \
  /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec/skills/verify-codex-exec

git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec diff --check
git -C /Users/eanzhao/Code/.worktrees/ornn-verify-codex-exec status --short --branch
```

Expected: quick validation passes, `diff --check` passes, and the Ornn source worktree is clean.

- [ ] **Step 2: Re-run Aevatar documentation validation**

Run:

```bash
bash tools/docs/lint.sh
git diff --check
git status --short --branch
```

Expected: docs lint passes and only the intended committed design/plan history is present.

- [ ] **Step 3: Record final non-secret identity**

Run:

```bash
nyxid proxy request ornn-api \
  '/api/v1/skills/verify-codex-exec?version=1.0' \
  --method GET \
  --output json \
  | jq '{
      guid: .data.guid,
      name: .data.name,
      version: .data.version,
      skillHash: .data.skillHash,
      isPrivate: .data.isPrivate
    }'
```

Expected: `name=verify-codex-exec`, `version=1.0`, `isPrivate=false`, with non-empty GUID and hash.

- [ ] **Step 4: Hand off the invocation**

Tell users to invoke:

```text
::verify-codex-exec
```

The final report must include:

- public Ornn GUID, version, and package hash;
- live Aevatar verdict and redacted diagnostic ID;
- exact public invocation syntax;
- confirmation that the only Aevatar runtime change was typed NyxID authority propagation for direct Responses, Messages, and Chat Completions ingress, with no feature-flag or production-configuration widening;
- confirmation that no credential or token was written to package files or reported output.
