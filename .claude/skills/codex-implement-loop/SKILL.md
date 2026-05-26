---
name: codex-implement-loop
description: Unattended sequential implementation loop driven by codex CLI + Claude subagent reviewer. Walks every issue under a GitHub milestone in order; each issue gets implement (codex) → stacked PR → review (subagent) → fix (codex if needed) until reviewer passes; then advances to the next issue. Use when user wants the full milestone landed as a stacked PR train with /loop dynamic wakeups.
---

# Codex Implement Loop — Unattended Sequential Milestone Mode

You are the **Controller**. You never write production code yourself. You orchestrate `codex exec` subprocesses for implement/fix work and `Agent` (subagent) calls for review work. Each issue is processed strictly sequentially in its own git worktree; PRs stack so issue N's PR base is issue N-1's PR head branch (issue 1's base is `review_base_branch`, defaulting to `dev`).

Each `/loop` wakeup runs **one iteration tick**: inspect `.implement-loop/state.json`, advance whichever phase is ready, schedule the next wakeup. Stop when every issue is in `issues_done` or any issue exceeded its review-round budget.

This skill complements `codex-refactor-loop` (parallel batched refactor work driven by audit). Use this skill when the user wants:
- Sequential issue-driven work (milestone → stacked PR train)
- A Claude subagent — not codex — as the reviewer
- One codex active at a time (no parallel cluster batches)
- Strictly stacked PRs (issue 1 → dev; issue N → issue N-1)

---

## Quick start

```bash
# user types:
/loop 完全无人值守完成 milestone <name-or-number> 下所有 issue
```

First wakeup → bootstrap state from milestone, dispatch first issue's implement codex, schedule fallback wakeup, end turn.

Subsequent wakeups → read state, advance whatever phase is ready (implement → pr_open → review → fix → review → done), schedule next wakeup.

---

## Phase 0 — Bootstrap (first wakeup only)

If `.implement-loop/state.json` does not exist:

```bash
mkdir -p .implement-loop/{logs,runs,prompts,worktrees,reviews}
```

Resolve inputs from the user's `/loop` prompt:

- **Milestone**: parse `milestone <X>` from the prompt. `X` can be a number (`47`) or a name (`router-rollout`). Both work with `gh issue list --milestone <X>`.
- **Repo**: default `aevatarAI/aevatar`. Override only if the prompt says so.
- **Review base branch**: default `dev`. Override if the prompt says "target main" / "base off X".
- **Trunk branch**: `git branch --show-current` at bootstrap time. Used to remember where the loop was started (for diagnostic logs only; the loop itself does not push to it).

Fetch the planned issues:

```bash
gh issue list --repo "<repo>" --milestone "<milestone>" \
  --state open --limit 200 \
  --json number,title,labels,body \
  --jq 'sort_by(.number) | .[] | {number, title}'
```

**Ordering**: default ascending by issue number. If the user's prompt says "in order X, Y, Z" or "by label priority", respect that — but record the chosen ordering in `state.issues_planned[].order_index` so resume after crash is deterministic.

Write initial `state.json` (see [REFERENCE.md](REFERENCE.md) for the full schema):

```json
{
  "schema_version": 1,
  "loop_started_at": "<ISO8601>",
  "repo": "aevatarAI/aevatar",
  "milestone": "<resolved name or number>",
  "trunk_branch": "<current branch at bootstrap>",
  "review_base_branch": "dev",
  "branch_prefix": "feat/<YYYY-MM-DD>_issue-",
  "max_review_rounds": 5,
  "phase": "implement",
  "issues_planned": [
    {"number": 671, "title": "...", "order_index": 0},
    {"number": 672, "title": "...", "order_index": 1}
  ],
  "issues_done": [],
  "issues_failed": [],
  "current_issue": null
}
```

**Branch prefix**: the default `feat/<YYYY-MM-DD>_issue-` matches CLAUDE.md's branch-naming rule (`<type>/YYYY-MM-DD_<purpose>`). The full branch becomes `feat/2026-05-19_issue-671`. Resolve the date **once at bootstrap** (so all issues share the same date prefix and the stack reads chronologically); store the resolved prefix in `state.branch_prefix`. Do not re-resolve mid-loop.

PushNotification: "implement-loop bootstrap: milestone=<X> (N issues planned), first PR will target <review_base_branch>."

Advance: set `current_issue` to `issues_planned[0]` with `phase = "implement"`, jump to Phase 1.

---

## Phase 1 — Implement (one codex in the current issue's worktree)

For `current_issue`:

1. **Resolve base branch** for the worktree (= what the worktree branch is created from):
   - If `current_issue` is `issues_planned[0]` → base = `review_base_branch` (e.g., `dev`).
   - Else → base = `issues_done[-1].branch` (previous issue's branch, still open as a PR).
   - Fetch the base ref first so the worktree starts from a known commit:
     ```bash
     cd "$REPO_ROOT" && git fetch origin "<base_branch>:<base_branch>" 2>/dev/null \
       || git fetch origin "<base_branch>"
     ```

2. **Create worktree** off the resolved base (do NOT use `HEAD`; HEAD might be unrelated):
   ```bash
   BRANCH="${branch_prefix}${issue_number}"   # e.g. feat/2026-05-19_issue-671
   git worktree add -b "$BRANCH" \
     .implement-loop/worktrees/issue-${issue_number} \
     "origin/<base_branch>"
   ```

3. **Materialize implement prompt** by copying `prompts/implement.md` and replacing placeholders (see [prompts/implement.md](prompts/implement.md)):
   - `{{issue_number}}` / `{{issue_title}}` / `{{repo}}`
   - `{{worktree_path}}` / `{{branch}}` / `{{base_branch}}`
   - `{{summary_output_path}}` = `$REPO_ROOT/.implement-loop/runs/implement-issue-${issue_number}.md`

   Save the materialized prompt to `.implement-loop/prompts/implement-issue-${issue_number}.md`.

4. **Dispatch**:
   ```bash
   .claude/skills/codex-implement-loop/scripts/spawn-codex.sh \
     --cd "$WORKTREE_PATH" \
     --add-dir "$REPO_ROOT" \
     --prompt .implement-loop/prompts/implement-issue-${issue_number}.md \
     --log .implement-loop/logs/implement-issue-${issue_number}.log \
     --timeout 5400
   ```
   Use Bash with `run_in_background: true`. 5400s (90 min) is the recommended budget for an issue-sized implement (per this skill's spawn wrapper rules; 3600s is the absolute minimum).

5. Record `current_issue.phase = "implement"`, save `bg_task` id. Schedule wakeup 1500–1800s as safety net. **End turn.**

When task notification fires → controller validation:

- a. Tail the log for `IMPLEMENT_DONE:<issue-number>:<status>` where `status ∈ {ok, partial, blocked}`.
- b. Verify the summary file `runs/implement-issue-<N>.md` exists and contains "Files changed" + "Tests run" sections.
- c. Verify `git -C <worktree> status --porcelain | wc -l > 0` (codex actually changed something).
- d. If marker missing or summary missing or no diff → re-dispatch ONCE with a corrected prompt header ("previous attempt returned no marker / empty diff; produce the marker and a real diff this run"). Second failure → move to `issues_failed` with reason `implement-no-output`, stop loop, PushNotification.

If `status == ok` → advance to Phase 2.
If `status == partial` or `blocked` → read the summary's "Blockers" section; if blocker is environmental (e.g., missing tool) → PushNotification + stop; if blocker is design ambiguity → re-dispatch ONCE with the ambiguity called out; second `partial`/`blocked` → `issues_failed` reason `implement-blocked`, stop.

---

## Phase 2 — Open PR (controller, not codex)

**cwd discipline (critical)**: `git push`, `gh pr create`, and any subsequent git mutation MUST run from `$REPO_ROOT`, never from the worktree directory. The harness persists Bash cwd across invocations; an earlier `cd .implement-loop/worktrees/issue-N` will leak into the next call. Always prefix trunk-side commands with `cd "$REPO_ROOT" && …` OR run them in a separate Bash invocation. Symptom of leak: `gh pr create` reporting "no commits between …" when the worktree branch clearly has commits.

For `current_issue` once Phase 1 finishes `ok`:

1. **Commit in worktree** (one commit per issue; the loop is not designed for issue-internal multi-commit history):
   ```bash
   cd "$WORKTREE_PATH" && git add -A && \
     git commit -m "$(cat <<'EOF'
Issue #${issue_number}: ${issue_title}

Implemented per .implement-loop/runs/implement-issue-${issue_number}.md.

Closes #${issue_number}

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
   ```

2. **Local quick gate** (still in worktree). Mandatory; failure flips the issue back to fix-codex without opening a PR:
   ```bash
   cd "$WORKTREE_PATH" && \
     dotnet build aevatar.slnx --nologo 2>&1 | tail -20 && \
     bash "$REPO_ROOT/tools/ci/architecture_guards.sh" && \
     bash "$REPO_ROOT/tools/ci/test_stability_guards.sh"
   ```
   On fail → undo the commit `git reset --soft HEAD~1`, re-dispatch implement codex with the failure log appended to the prompt (`previous attempt failed local gate: <log>`). After 2 gate failures in a row → `issues_failed` reason `gate-stuck`.

3. **Push branch from $REPO_ROOT**:
   ```bash
   cd "$REPO_ROOT" && git push -u origin "$BRANCH"
   ```

4. **Open PR** with base = the resolved base branch for this issue:
   ```bash
   PR_BODY=$(cat <<EOF
## Issue
Closes #${issue_number} — ${issue_title}

## Implementation summary
See \`.implement-loop/runs/implement-issue-${issue_number}.md\`.

## Stacked-PR position
- Base: \`${base_branch}\` $(if [[ "${base_branch}" != "${review_base_branch}" ]]; then echo "(previous issue's branch)"; fi)
- Head: \`${BRANCH}\`
- Auto-loop iteration: implement-loop / milestone ${milestone}

🤖 Generated by codex-implement-loop. Reviewer is a Claude subagent (see PR comments for round-N review reports).
EOF
)

cd "$REPO_ROOT" && \
  PR_NUMBER=$(gh pr create \
    --repo "$REPO" \
    --base "$BASE_BRANCH" \
    --head "$BRANCH" \
    --title "Issue #${issue_number}: ${issue_title}" \
    --body "$PR_BODY" \
    --json number --jq .number)
```

**Idempotency**: if a PR for the same head→base already exists (the loop is resuming after crash mid-Phase-2), reuse it:
```bash
existing=$(gh pr list --head "$BRANCH" --base "$BASE_BRANCH" --state open --json number --jq '.[0].number')
PR_NUMBER=${existing:-$(gh pr create ...)}
```

5. Save `current_issue.pr_number = $PR_NUMBER`, `current_issue.pr_base_branch = $BASE_BRANCH`, advance `phase = "review"`, `review_round = 1`.

PushNotification: "issue #N implement-done; PR #M opened against `<base>`. Dispatching subagent reviewer."

---

## Phase 3 — Review (Claude subagent, synchronous)

The reviewer is a **subagent**, not codex. Subagent calls are synchronous in the controller's turn — there is no task-notification wait. Dispatch with the Agent tool:

1. **Materialize review prompt**: copy `prompts/review.md` → `.implement-loop/prompts/review-pr${PR_NUMBER}-round${R}.md`. Replace `{{issue_number}}`, `{{issue_title}}`, `{{pr_number}}`, `{{base_branch}}`, `{{head_branch}}`, `{{review_round}}`, `{{review_output_path}}` (= `$REPO_ROOT/.implement-loop/reviews/pr${PR_NUMBER}-round${R}.md`).

2. **Dispatch the subagent** (one call, synchronous, blocks the controller turn):
   ```
   Agent(
     subagent_type="general-purpose",
     description="Review PR #${PR_NUMBER} round ${R}",
     prompt=<contents of the materialized review prompt — read with Read, paste verbatim>
   )
   ```
   The prompt instructs the subagent to:
   - Read the PR diff (`gh pr diff ${PR_NUMBER}`) and the original issue (`gh issue view ${issue_number}`).
   - Read the implement summary at `.implement-loop/runs/implement-issue-${issue_number}.md`.
   - Read `CLAUDE.md` + any cited `docs/canon/` references.
   - Write a structured verdict file at the specified `review_output_path`.
   - Return a one-line summary: `REVIEW_VERDICT:<pass|rework|abort>:<headline>`.

3. **Parse the subagent's return text** for the `REVIEW_VERDICT:` line. If absent → re-dispatch ONCE with `"previous review returned no verdict line; restate the verdict on the final line"`. Second miss → escalate to PushNotification, mark `current_issue.phase = "review-stuck"`, stop loop.

4. **Post the review report as a PR comment** (regardless of verdict — full traceability):
   ```bash
   cd "$REPO_ROOT" && \
     gh pr comment "$PR_NUMBER" --repo "$REPO" \
       --body-file ".implement-loop/reviews/pr${PR_NUMBER}-round${R}.md"
   ```

5. **Branch on verdict**:
   - `pass` → mark `current_issue.phase = "done"`, move to Phase 5.
   - `rework` → advance to Phase 4 (fix codex). Increment `review_round` lives there.
   - `abort` → review identified a design-level problem unsuited to auto-fix. Move issue to `issues_failed` reason `review-abort:<headline>`, stop loop, PushNotification with the verdict file path + PR URL.

6. **Round cap**: if `review_round > max_review_rounds` (default 5), treat as `abort` with reason `review-round-cap`. The fix loop is bounded so a single issue can't burn unbounded codex time.

---

## Phase 4 — Fix (codex in the same worktree)

Triggered only when Phase 3 returned `rework`. The worktree from Phase 1 is reused (codex iterates on the same branch + same files).

1. **Materialize fix prompt**: copy `prompts/fix.md` → `.implement-loop/prompts/fix-pr${PR_NUMBER}-round${R}.md`. Replace placeholders: `{{issue_number}}`, `{{pr_number}}`, `{{worktree_path}}`, `{{branch}}`, `{{review_report_path}}` (= the file from Phase 3), `{{review_round}}`, `{{fix_summary_path}}` (= `$REPO_ROOT/.implement-loop/runs/fix-pr${PR_NUMBER}-round${R}.md`).

2. **Dispatch fix codex**:
   ```bash
   .claude/skills/codex-implement-loop/scripts/spawn-codex.sh \
     --cd "$WORKTREE_PATH" \
     --add-dir "$REPO_ROOT" \
     --prompt .implement-loop/prompts/fix-pr${PR_NUMBER}-round${R}.md \
     --log .implement-loop/logs/fix-pr${PR_NUMBER}-round${R}.log \
     --timeout 3600
   ```
   `run_in_background: true`. Schedule fallback wakeup 1500s. **End turn.**

3. On task notification, check log for `FIX_DONE:${pr_number}:round-${R}:<status>` where `status ∈ {ok, blocked}`.
   - `ok` → controller continues to step 4.
   - `blocked` → read the fix summary's "Blocked" section. If blocker is "review demand contradicts CLAUDE.md / would scope-creep" → escalate (`issues_failed` reason `fix-blocked:<short>`, PushNotification, stop). Do NOT auto-loop on `blocked`.

4. **Local quick gate again** (matches Phase 2 step 2):
   ```bash
   cd "$WORKTREE_PATH" && \
     dotnet build aevatar.slnx --nologo 2>&1 | tail -20 && \
     bash "$REPO_ROOT/tools/ci/architecture_guards.sh" && \
     bash "$REPO_ROOT/tools/ci/test_stability_guards.sh"
   ```
   On fail → re-dispatch fix codex with the gate log appended. After 2 gate failures in the same round → `issues_failed` reason `fix-gate-stuck`.

5. **Commit on top** of the existing issue commit (additive history; do NOT amend the original commit — reviewers need to see what changed each round):
   ```bash
   cd "$WORKTREE_PATH" && git add -A && \
     git commit -m "Fix review round ${R} on PR #${PR_NUMBER}"
   ```

6. **Push from $REPO_ROOT** (updates the open PR; no re-create needed):
   ```bash
   cd "$REPO_ROOT" && git push origin "$BRANCH"
   ```

7. Increment `current_issue.review_round`, set `phase = "review"`, **loop back to Phase 3**.

---

## Phase 5 — Advance pointer

When Phase 3 returned `pass`:

1. Append `current_issue` to `issues_done` with metadata:
   ```json
   {
     "number": 671,
     "title": "...",
     "branch": "feat/2026-05-19_issue-671",
     "pr_number": 700,
     "review_rounds_used": 2,
     "passed_at": "<ISO8601>"
   }
   ```

2. **Do NOT merge the PR.** PRs stay stacked open for human review/merge. The next issue's worktree branches off the open PR's head branch directly.

3. **Do NOT remove the worktree.** The next issue's worktree is independent. Optionally remove old worktrees after the whole milestone finishes — but in-flight, keep them so a reviewer can `cd` into one and inspect.

   (Skip worktree cleanup unless `state.cleanup_done_worktrees == true` is set, which a user can flip mid-loop.)

4. **Pick next issue**: first member of `issues_planned` not in `issues_done ∪ issues_failed`. If none → loop is finished; jump to **Loop control / Stop**.

5. Set `current_issue` = next issue with `phase = "implement"`, `review_round = 0`, `pr_number = null`, `bg_task = null`. Jump to Phase 1.

PushNotification (each pointer advance): "issue #N passed review (round R); advancing to issue #M (PR base = `<branch>`)."

---

## Loop control

- **Stop conditions** (no more ScheduleWakeup, send final PushNotification with summary):
  - Every `issues_planned` entry is in `issues_done ∪ issues_failed`, OR
  - Any issue moved to `issues_failed` (the stack semantics break if we skip an issue, so failure halts the train — operator decides whether to remove the bad issue from the milestone and resume).
- **Wakeup cadence**:
  - Primary: harness task notifications on codex exit (Phase 1 / Phase 4).
  - Subagent reviews (Phase 3) are synchronous — no wait needed.
  - Fallback: `ScheduleWakeup` 1500–1800s after each codex dispatch.
- **Resume after crash**: read `state.json` + `current_issue.phase` to know where to pick up. Each phase is idempotent (`gh pr list --head … --state open` for Phase 2, `git status` for Phase 1, file presence for Phase 3 review output, etc.). See [REFERENCE.md](REFERENCE.md) "Recovery playbook".

---

## Hard rules (controller-level, also embedded in every codex prompt)

1. **Sequential only**: never dispatch two codexes concurrently in this loop. The PR stack is linear by construction; parallel work breaks the base-branch chain.
2. **No PR merging**: the controller never runs `gh pr merge`. The whole stack stays open for human review.
3. **Controller owns git topology**: codex prompts must not run `git commit` / `git push` / `git checkout` / `gh pr create` (per this skill's controller rules). Codex stages changes; controller commits and pushes.
4. **Hard cap on rework**: `max_review_rounds` (default 5) per issue. After cap, fail the issue and halt the train — don't burn unbounded codex time on one issue.
5. **No external repo changes**: codex prompts forbid touching NyxID / chrono-* (per CLAUDE.md "外部仓库无改动权").
6. **No `[Skip]` / disabled tests** to make CI green.
7. **No `Task.Delay`-based test pacing** — tests must use deterministic awaiters (per `tools/ci/test_stability_guards.sh`).
8. **Subagent reviewer is independent**: the review subagent must not see the controller's own conclusions; pass it only the materialized review prompt + tool access (Read / Bash / Grep). It must reach a verdict from PR diff + issue body + CLAUDE.md, nothing else.

---

## Files

- [prompts/implement.md](prompts/implement.md) — codex prompt for implementing one issue
- [prompts/review.md](prompts/review.md) — subagent prompt for reviewing one PR
- [prompts/fix.md](prompts/fix.md) — codex prompt for fixing review findings
- [scripts/spawn-codex.sh](scripts/spawn-codex.sh) — `codex exec` wrapper (enforces ≥ 3600s timeout)
- [REFERENCE.md](REFERENCE.md) — state schema, branch naming, recovery playbook, comparison to other loops
