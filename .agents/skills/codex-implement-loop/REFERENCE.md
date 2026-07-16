# codex-implement-loop — Reference

Detailed specifications, edge cases, and recovery playbook. Main workflow is in [SKILL.md](SKILL.md).

## State schema (`.implement-loop/state.json`)

```json
{
  "schema_version": 1,
  "loop_started_at": "<ISO8601>",
  "repo": "aevatarAI/aevatar",
  "milestone": "<resolved milestone name or number>",
  "trunk_branch": "<branch where /loop was started; informational>",
  "review_base_branch": "dev",
  "branch_prefix": "feat/<YYYY-MM-DD>_issue-",
  "max_review_rounds": 5,
  "cleanup_done_worktrees": false,
  "phase": "implement | pr_open | review | fix | done",

  "issues_planned": [
    {"number": 671, "title": "...", "order_index": 0},
    {"number": 672, "title": "...", "order_index": 1}
  ],

  "issues_done": [
    {
      "number": 671,
      "title": "...",
      "branch": "feat/2026-05-19_issue-671",
      "pr_number": 700,
      "review_rounds_used": 2,
      "passed_at": "<ISO8601>"
    }
  ],

  "issues_failed": [
    {
      "number": 673,
      "phase": "review | implement | fix | gate",
      "reason": "review-round-cap | implement-blocked | gate-stuck | fix-blocked:conflict:... | review-abort:...",
      "pr_number": 702,
      "branch": "feat/2026-05-19_issue-673",
      "failed_at": "<ISO8601>"
    }
  ],

  "current_issue": {
    "number": 672,
    "title": "...",
    "worktree": ".implement-loop/worktrees/issue-672",
    "branch": "feat/2026-05-19_issue-672",
    "base_branch": "feat/2026-05-19_issue-671",
    "pr_number": null,
    "phase": "implement | pr_open | review | fix | done",
    "review_round": 0,
    "bg_task": null,
    "last_review_verdict": null,
    "last_review_path": null,
    "last_fix_summary_path": null,
    "started_at": "<ISO8601>"
  }
}
```

### Field semantics

- `branch_prefix`: resolved once at bootstrap to lock the date stamp across the whole milestone run; do **not** re-resolve mid-loop or stacked PR branches will have inconsistent prefixes.
- `current_issue.base_branch`: for the first issue this equals `review_base_branch`; for subsequent issues it equals `issues_done[-1].branch`. The loop computes this when advancing the pointer (Phase 5); never trust an older `base_branch` if the state was edited by hand.
- `current_issue.phase` vs top-level `phase`: top-level is a coarse pointer; `current_issue.phase` is authoritative for "where are we in this issue's mini state machine".
- `review_round`: starts at 1 when Phase 3 first runs for an issue. Incremented by the fix loop (Phase 4 → back to Phase 3). When `review_round > max_review_rounds`, the issue is marked failed.

## Branch + PR naming

- **Branch**: `${branch_prefix}${issue_number}`, e.g., `feat/2026-05-19_issue-671`. Matches CLAUDE.md "提交与 PR" rule `<type>/YYYY-MM-DD_<purpose>`.
- **PR title**: `Issue #<num>: <issue title>`. Keeps GitHub UI scan-able and links the PR back to the milestone view.
- **PR body**: includes `Closes #<num>` (auto-closes the issue on PR merge), pointer to the implement summary path, and the stacked-PR position (base + head).
- **Commit message** (controller-authored): `Issue #<num>: <issue title>` headline + `Closes #<num>` footer + `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`. Fix-round commits use `Fix review round <R> on PR #<num>`.

## PR stack topology

```
dev
 └── feat/2026-05-19_issue-671  ← PR #700 (base: dev)
      └── feat/2026-05-19_issue-672  ← PR #701 (base: feat/...issue-671)
           └── feat/2026-05-19_issue-673  ← PR #702 (base: feat/...issue-672)
                └── ...
```

PRs stay **open** through the whole loop. A human reviews/merges the stack bottom-up; merging PR #700 retargets PR #701's GitHub base to `dev` automatically (GitHub feature). The loop is agnostic to whether human merges any of them during the run.

## Worktree lifecycle

- Created in Phase 1 with `git worktree add -b <branch> .implement-loop/worktrees/issue-<N> origin/<base_branch>`.
- Reused across all fix rounds for the same issue (preserves codex's working state and avoids re-cloning).
- **Not removed** when the issue passes — the next issue's worktree is a separate directory, so leaving the old one around lets human inspectors `cd` in.
- Cleanup at loop end is optional: set `state.cleanup_done_worktrees = true` (the user can flip this mid-loop) and the controller will `git worktree remove` each `issues_done[*]` worktree after the loop's final stop.

## Recovery playbook

### Loop crashed mid-implement

- Log file ends without `IMPLEMENT_DONE:` marker.
- On resume: re-dispatch the implement codex with the same prompt + a header note `"previous attempt crashed at <last log line>; resume and produce the marker"`. Codex sees the worktree's existing (partial) changes and continues.
- After 2 consecutive crashes → `issues_failed` reason `implement-crashed`, stop, PushNotification.

### Loop crashed mid-PR open

- Detect: `current_issue.phase == "pr_open"` and `current_issue.pr_number == null`.
- Resume: check if a PR for `head=<branch> base=<base_branch>` already exists (`gh pr list`); if yes, adopt its number; if no, run the gate + create steps again (the worktree commit may already exist — check `git log -1 --format=%H origin/<branch>` to detect).

### Loop crashed mid-review

- Subagent calls are synchronous, so a crash here means the controller died inside the Agent call.
- Resume: re-dispatch the review subagent for the same `(pr_number, review_round)`. The subagent rewrites `{{review_output_path}}`. The controller treats only the **latest** review-output file as authoritative (overwrite, don't append).

### Loop crashed mid-fix

- Log file ends without `FIX_DONE:` marker.
- Resume: re-dispatch fix codex with header `"previous fix attempt crashed; resume; the review findings have not been re-issued so apply the original ones"`. Worktree state preserves what fix codex already did.

### Subagent reviewer hung or returned no `REVIEW_VERDICT:` line

- Re-dispatch once with header `"the previous review missed the required final line REVIEW_VERDICT:<verdict>:<headline>; produce it"`.
- Second miss → `current_issue.phase = "review-stuck"`, halt loop, PushNotification ("subagent reviewer not producing verdict — manual decision needed").

### Reviewer keeps oscillating between `rework` and `pass` across rounds

- Detection: same `F<N>` titles appearing/disappearing across rounds.
- Mitigation: round comparison block in `review.md` requires the subagent to explicitly account for prior-round findings. If oscillation persists past 3 rounds, treat as `abort` ("review unstable — manual decision needed") and halt loop.

### Reviewer rejects something fix codex blocks (cycle)

- Fix codex emits `FIX_DONE:...:blocked` + `FIX_BLOCKED_REASON:conflict:...` or `human-decision:...`.
- Controller halts loop immediately (does NOT re-dispatch review). PushNotification with the blocker reason + PR URL.
- Operator decides: edit the PR manually, mark the issue as `issues_failed` and resume, or close the loop.

### Base branch moved while issue was in flight

- Symptom: PR shows extra commits from the base branch that the issue didn't author.
- Mitigation: do NOT auto-rebase the in-flight issue; that conflates fix work with rebase work and confuses the reviewer. Mark the issue `issues_failed` reason `base-drift`, halt loop, surface via PushNotification. Operator can rebase manually and resume.
- Exception: if `state.auto_rebase_on_base_drift == true` is set (user opt-in), controller rebases the worktree branch onto the new base in `$REPO_ROOT` cwd discipline, re-runs the local gate, force-pushes with `--force-with-lease`, and only proceeds to review afterward.

### cwd leak in Phase 2 / 4 push or merge ("Already up to date" / "no commits between …")

- Symptom: a git mutation that should affect the trunk-side branch silently no-ops because the harness's persistent Bash cwd is still inside `.implement-loop/worktrees/issue-N`.
- Fix: always prefix the trunk-side command with `cd "$REPO_ROOT" &&` OR run it in a separate Bash invocation after the worktree-scoped commit. Verify after every push/merge: `cd "$REPO_ROOT" && git log -1 --oneline origin/<branch>` must show the new commit SHA.

### Resuming after a manual fix on the PR (human pushed a commit)

- The loop tolerates this: when controller wakes and runs `gh pr view` for status, it will see new commits not authored by the loop. It treats them as if a fix-round happened, re-runs review with the next round number, and proceeds.
- Operator should NOT delete the worktree manually — the loop's next iteration needs it. If operator wants to abandon the issue, mark it `issues_failed` reason `manual-abort` and resume; the loop skips it on the next pointer-advance.

### Resuming after the loop was stopped manually

- `state.json` retains the last `current_issue`. On `/loop` resume, the controller inspects `current_issue.phase` and advances from there. Idempotent operations make every phase safe to retry (PR-create checks for existing PR, worktree-create errors on duplicate branch handled, etc.).

## Subagent dispatch — implementation notes

Phase 3 uses the Agent tool synchronously. To keep the call self-contained:

```
prompt_body=$(cat .implement-loop/prompts/review-pr<PR>-round<R>.md)
Agent({
  subagent_type: "general-purpose",
  description: "Review PR #<PR> round <R>",
  prompt: prompt_body
})
```

- `description` must be ≤ 5 words but specific enough that the user can identify the agent in the activity feed.
- The subagent has access to the same tools the parent has (Read / Bash / Grep / Glob / Edit not strictly needed but harmless).
- The subagent reads files directly with Read (faster than asking codex). It uses Bash for `gh pr diff / gh issue view`.
- The subagent **must not** invoke its own Agent / sub-subagent calls — keep the review pass single-layer. The prompt makes this explicit.

If the team later prefers a different reviewer model (claude-opus / claude-sonnet split, or a real `code-reviewer` agent type if/when registered), update only the `subagent_type` field in the controller's Agent call; the prompt template stays the same.

## Comparison to `codex-refactor-loop`

| Aspect | `codex-refactor-loop` | `codex-implement-loop` (this skill) |
|---|---|---|
| Work source | Audit codex finds CLAUDE.md violations | GitHub milestone issues (curated by humans) |
| Parallelism | Multi-cluster batches in parallel worktrees | One issue at a time, strictly sequential |
| Reviewer | Codex (`verify` phase) or multi-codex consensus | Claude subagent (Agent tool, synchronous) |
| PR topology | Stacked OR single OR per-cluster against integration | Strict stack: issue N's PR base = issue N-1's PR head |
| Merge | Controller merges cluster PRs into integration | Never merges; PRs stay open for human |
| State dir | `.refactor-loop/` | `.implement-loop/` |
| Branch prefix | `refactor/iterN-<cluster-id>` | `feat/<YYYY-MM-DD>_issue-<num>` |
| Codex roles | audit / implement / verify / remote-ci-fix / test-add | implement / fix (no audit; no remote-ci-fix; no test-add — tests are part of implement) |
| Failure halt | One cluster failing doesn't halt others | Any failure halts the train (stack break) |

Use `codex-refactor-loop` when the work is open-ended (auditing the codebase for violations); use `codex-implement-loop` when the work is a defined milestone of issues that need to land as a stacked review-able PR train.

## Comparison to `refactor-team`

`refactor-team` is an in-process Claude Agent orchestration (audit-fix-review all as subagents in one session). It's the closest cousin to the Phase 3 reviewer in this skill, but `refactor-team` doesn't:
- spawn `codex exec` (it's pure Claude Agent), so no OS-level parallelism;
- handle issue/milestone input;
- write to `.refactor-loop/` or `.implement-loop/` state;
- open or stack PRs.

Use `refactor-team` for a single human-supervised cycle; this skill for unattended multi-issue runs.

## Loop termination

Stop when **either**:

- `issues_planned ⊂ issues_done ∪ issues_failed` (every planned issue is accounted for), OR
- `issues_failed` gained a new entry (stack semantics break → halt train, operator decides).

On stop:
- Omit `ScheduleWakeup`.
- Send a one-line `PushNotification`: `implement-loop done: <N done> / <M failed> / <K total>. Stack head: PR #<num>.`
- Leave `.implement-loop/` intact for post-mortem (state.json + logs + reviews + runs).
- Worktrees stay unless `state.cleanup_done_worktrees == true`.

## Re-running the loop on the same milestone

Re-invoking `/loop` on a milestone that already has some PRs from a prior run:

- The controller reads existing `state.json` (if any) → resumes from `current_issue.phase`.
- If `state.json` is missing but the milestone has open PRs from a prior run on disk, do **not** auto-adopt them. Bootstrap a fresh state.json, plan all issues, and skip ones whose `gh pr list --search "Issue #<N>"` returns a non-closed PR (treat them as already done). Surface skipped count in the bootstrap PushNotification.
- For a clean re-run, the operator deletes `.implement-loop/` + closes/deletes the prior PRs themselves; the loop will not destructively touch existing PRs without `state.cleanup_done_worktrees == true` plus a missing state.json.
