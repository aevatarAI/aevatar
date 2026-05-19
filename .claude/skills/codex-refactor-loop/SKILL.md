---
name: codex-refactor-loop
description: Unattended three-phase refactor loop (analyze → implement → verify) driven by codex CLI in isolated git worktrees. Use when user wants fully autonomous parallel refactoring against CLAUDE.md violations, with /loop dynamic wakeups and per-cluster worktree merges.
---

# Codex Refactor Loop — Unattended Three-Phase Mode

You are the **Controller**. You never edit production code yourself. You orchestrate `codex exec` subprocesses that do all analysis, implementation, and verification work in isolated git worktrees.

Each `/loop` wakeup runs **one iteration tick**: inspect `.refactor-loop/state.json`, advance whichever phase is ready, schedule the next wakeup. Stop when `clusters_planned == clusters_done`.

This skill complements `refactor-team` (Agent-subagent based). Use this skill when the user wants:
- True OS-level parallelism via worktrees
- Each phase as an independent `codex exec` process (not a Claude subagent)
- Dynamic `/loop` self-pacing rather than fixed cron

---

## Quick start

```bash
# user types:
/loop <task description... 完全无人值守模式>
```

First wakeup → bootstrap state, dispatch audit codex, schedule fallback wakeup, end turn.

Subsequent wakeups → read state, advance any cluster that's ready, schedule next wakeup.

---

## Phase 0 — Bootstrap (first wakeup only)

If `.refactor-loop/state.json` does not exist:

```bash
mkdir -p .refactor-loop/{logs,runs,clusters,prompts,worktrees,state}
```

Write initial `state.json`:

```json
{
  "schema_version": 1,
  "trunk_branch": "auto-refact-dev",
  "integration_branch": "auto-refact-dev",
  "review_base_branch": "dev",
  "pr_mode": "stacked",
  "max_parallel_clusters": 3,
  "iteration": 1,
  "phase": "audit",
  "clusters_planned": [],
  "clusters_active": [],
  "clusters_done": [],
  "clusters_failed": []
}
```

**Default integration branch**: `auto-refact-dev`. This is the long-lived branch where all auto-refactor cluster PRs land before rolling up to `dev`. On a fresh loop:

```bash
# Idempotent setup — safe to re-run
git fetch origin
git checkout -B auto-refact-dev origin/auto-refact-dev 2>/dev/null \
  || git checkout -b auto-refact-dev origin/dev
git push -u origin auto-refact-dev 2>/dev/null || true
```

Override only when the user explicitly names a different integration branch (e.g., to test a new audit prompt without polluting the canonical one). Existing loops on a different branch can keep their name; the default applies to **new** Phase 0 bootstraps only.

**`pr_mode` choice (set in Phase 0; do not change mid-loop)**:

- `"stacked"` (**default**): each cluster opens its own PR. Hard-dep clusters stack (PR B's base = PR A's branch); soft-dep / independent clusters PR against `integration_branch`. Integration branch eventually opens one rollup PR to `review_base_branch`. Reviewer sees small per-cluster PRs and can ack independently; cost is rebase-on-reject when an upstream cluster is changed. This is the right shape for typical refactor loops (3+ clusters, reviewable independently).
- `"single"`: all clusters merge to `integration_branch` and a single PR targets `review_base_branch`. Simple; reviewer sees one big PR. Use only when the loop is expected to produce ≤ 2 clusters or the user explicitly asks for a single PR.

If the user doesn't specify, default `"stacked"` and surface in bootstrap PushNotification: "Using stacked-PR mode; pass `pr_mode: single` to override."

Create top-level TaskCreate items: audit / dispatch / merge.

---

## Phase 1 — Audit (one codex + controller validation)

1. Copy `prompts/audit.md` (this skill's template) to `.refactor-loop/prompts/audit-iter-N.md`.
2. Replace `{{iteration}}` placeholder.
3. Dispatch:

   ```bash
   .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
     --cd "$REPO_ROOT" \
     --prompt .refactor-loop/prompts/audit-iter-N.md \
     --log .refactor-loop/logs/audit-iter-N.log \
     --timeout 3600
   ```

   Use Bash with `run_in_background: true`. 3600s (60 min) is the project-wide minimum for codex jobs (see CLAUDE.md "Codex CLI 调用规范"); audit may legitimately need most of it to complete the coverage manifest.

4. Schedule wakeup 1500–1800s as safety net (task notification is primary wake).
5. **End turn.**

When task notification fires → **controller validation** before accepting the audit:

- a. Check log tail for the terminal marker: `AUDIT_DONE:...:<N>` or `AUDIT_INCOMPLETE:<reason>`.
- b. If `AUDIT_INCOMPLETE` → log reason, re-dispatch audit with the missing pieces called out in the prompt header (e.g., "previous audit returned INCOMPLETE because <reason>; deliver the missing artifact this run"). Do NOT proceed to Phase 2 with an incomplete audit.
- c. Verify the two output files exist: `audit-iter-N.md` AND `audit-iter-N-candidates.ndjson`. Missing either → treat as INCOMPLETE.
- d. Verify the candidate file has `>= 25` entries unless the audit body explicitly explains why every analyzer pack command returned 0 hits.
- e. Verify the audit body contains the 6 fixed-analyzer-pack commands by name with hit counts.
- f. Verify reject reasons cite a CLAUDE clause + per-candidate evidence (not blanket "covered by guard"). Sample 3 random rejects; if any lack evidence → INCOMPLETE.
- g. Verify `coverage_manifest.total_opened_files >= 60` with the documented sub-distribution.

Anti-anchoring: **do not** include phrases like "prefer 0", "loop saturated", "healthy signal" in the audit prompt body. These bias codex toward terminating instead of digging. Use the mechanical thresholds in `prompts/audit.md` as the only stop criteria.

After validation: read `audit-iter-N.md`, populate `clusters_planned`, split into batches (max `max_parallel_clusters` per batch) by **file/project disjointness**:

- Two clusters that touch the same `.csproj` or share a file path go in different batches.
- Two clusters that touch the same proto file → different batches.

### requires_design clusters → open GitHub issue, do NOT auto-implement

For every cluster with `requires_design: true`:

1. Open a GitHub issue via `gh issue create`:
   ```bash
   gh issue create \
     --title "[refactor-design] <cluster-id>: <one-line problem from audit>" \
     --label "refactor-design-needed,auto-loop" \
     --body "$(envsubst < .claude/skills/codex-refactor-loop/prompts/design-issue-body.md)"
   ```
   The body template at `prompts/design-issue-body.md` includes: the cluster's YAML block from audit, full evidence section, the audit's `Fix boundary` paragraph, and an explicit "decision needed" checklist (proto schema? new contract? backward-compat strategy? whether to split into multiple PRs?).
2. Record in state.json:
   ```json
   "design_pending": [
     {"cluster_id": "cluster-NNN", "issue_number": 234,
      "opened_at": "<ISO8601>", "last_checked": "<ISO8601>",
      "last_comment_count": 0, "status": "awaiting_design"}
   ]
   ```
3. Skip the cluster in Phase 2 (do NOT batch it).
4. PushNotification: "iter<N> opened design issue #<num> for cluster-<id>. Auto-loop paused on this cluster pending human design decision."

Update state, advance to Phase 2 (with requires_design clusters excluded).

---

## Phase 2 — Implement (parallel codexes, one per cluster in current batch)

For each cluster in the current batch:

1. Create worktree:

   ```bash
   git worktree add -b refactor/iterN-<cluster-id> \
     .refactor-loop/worktrees/<cluster-id> HEAD
   ```

2. Materialize prompt: copy `prompts/implement.md`, replace placeholders (`{{cluster_id}}`, `{{worktree_path}}`, `{{branch}}`, `{{old_pattern}}`, `{{new_principle}}`, `{{scope_paths}}`, `{{verification_hints}}`). Save to `.refactor-loop/prompts/implement-<cluster-id>.md`.

3. Dispatch via `spawn-codex.sh --cd <worktree>` with `--timeout 5400` (90 min).

4. Update `clusters_active` with `bg_task` id.

After all parallel dispatches, schedule wakeup 1800s safety net. **End turn.**

When each task notification fires → check log tail for `IMPLEMENT_DONE:<cluster-id>:<status>`:
- `ok` → advance that cluster to Phase 3 (verify).
- `partial` / `blocked` → move to `clusters_failed`, log reason, optionally re-dispatch with corrected prompt.

Do **not** advance the whole batch in lockstep; verify each cluster independently as soon as its implement finishes.

---

## Phase 3 — Verify (one codex per cluster, independent of implement codex)

For each cluster whose implement finished `ok`:

1. Materialize `prompts/verify.md` → `.refactor-loop/prompts/verify-<cluster-id>.md`.
2. Dispatch in the same worktree (verify reads `git diff HEAD`, runs full test/guard suite, gates merge):

   ```bash
   .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
     --cd <worktree> \
     --prompt .refactor-loop/prompts/verify-<cluster-id>.md \
     --log .refactor-loop/logs/verify-<cluster-id>.log \
     --timeout 3600
   ```

3. End turn after dispatching all ready verifies. Wait for task notifications.

Verify output marker: `VERIFY_DONE:<cluster-id>:<verdict>` where verdict ∈ `{pass, rework, abort}`.

- `pass` → advance to Phase 4 (merge).
- `rework` → re-dispatch implement codex with verifier's findings appended.
- `abort` → move to `clusters_failed`, surface in PushNotification.

---

## Phase 4 — Merge & Push (controller, not codex)

**cwd discipline (critical)**: `git merge`, `git push`, and `gh pr create` MUST run from `$REPO_ROOT`, never from a worktree directory. Cwd persists across Bash invocations in the harness, so chained commands that include `cd .refactor-loop/worktrees/<id>` leak cwd into the next call. Always either start the trunk-side command with `cd "$REPO_ROOT" && …` or run it in a separate Bash invocation after the worktree-scoped commit. If you see `Already up to date.` after a merge, that is the signature of cwd leak — diagnose and redo from `$REPO_ROOT`.

For each `pass` cluster, serially:

1. **Commit in worktree**: `cd <worktree> && git add -A && git commit -m "<msg>"`.

2. **Local CI on the cluster branch** (still in worktree):
   ```bash
   bash tools/ci/architecture_guards.sh
   bash tools/ci/test_stability_guards.sh
   # plus any cluster-specific guards from audit.verification_hints
   ```
   On fail → `git reset --soft HEAD~1` (undo the commit), mark cluster `rework`, re-dispatch implement codex with the failure log.

3. **Push cluster branch**: `cd $REPO_ROOT && git push origin refactor/iterN-<cluster-id>`.

4. **Branch off** by `pr_mode`:

### Phase 4a — `pr_mode: "single"`

5a. Merge cluster branch into `integration_branch`:
    ```bash
    cd "$REPO_ROOT" && git merge --no-ff refactor/iterN-<cluster-id> \
      -m "Merge cluster-<id>: <short title>"
    ```
6a. Re-run local CI on integration_branch (catches inter-cluster interaction).
7a. `git push origin <integration_branch>`.
8a. Goto Phase 5 (remote CI watch).

### Phase 4b — `pr_mode: "stacked"`

5b. **Choose PR base** per the cluster's `dependencies` field from the audit:
    - `dependencies: []` (independent, soft-dep, or batch-disjoint) → base = `integration_branch`.
    - `dependencies: ["cluster-XXX", ...]` (hard-dep — won't compile without the prerequisite) → base = the prerequisite cluster's branch (use the **first**, primary one; document others in PR description).

6b. **Open PR**:
    ```bash
    cd "$REPO_ROOT" && \
    gh pr create \
      --base "<base_branch>" \
      --head "refactor/iterN-<cluster-id>" \
      --title "<cluster id>: <short title>" \
      --body "$(cat .refactor-loop/runs/implement-<cluster-id>.md)
---
Auto-generated by codex-refactor-loop iter<N>.

Verify report: .refactor-loop/runs/verify-<cluster-id>.md
$(if [[ -n "<deps>" ]]; then echo "Depends on: <deps as PR links>"; fi)
$(if [[ "<soft_deps>" ]]; then echo "Related (no merge order required): <soft-dep links>"; fi)"
    ```

7b. Record the PR number in `state.clusters_active[i].pr_number`.
8b. **Stack rebase on upstream merge**: when an upstream (dependency) cluster's PR merges into `integration_branch`, immediately:
    - For each downstream cluster whose `dependencies` contained it:
      - `git -C <worktree> rebase --onto integration_branch <old_upstream_branch>` (or `gh pr edit <pr> --base integration_branch` if stacked-on-stacked is no longer needed).
      - Re-run local CI in worktree; on conflict, mark cluster `rework` and re-dispatch implement codex with conflict diff.
      - Force-push the cluster branch: `git push --force-with-lease origin refactor/iterN-<cluster-id>`.
9b. Goto Phase 5 (remote CI watch on the cluster's PR).
10b. After **all** iteration clusters have their PRs merged into `integration_branch`, ensure exactly one rollup PR exists from `integration_branch` to `review_base_branch`:
     ```bash
     gh pr list --head "<integration_branch>" --base "<review_base_branch>" --json number --jq '.[0].number'
     # If empty, gh pr create --base "<review_base_branch>" --head "<integration_branch>" --title "Refactor iter<N>: rollup" --body <scorecard.md>
     ```

After merge of the cluster branch into its target → `git worktree remove .refactor-loop/worktrees/<cluster-id>`. **Do NOT** delete the cluster branch yet under `stacked` mode — downstream PRs may still reference it as base; let GitHub auto-delete on merge.

If no clusters left in current batch → start next batch (Phase 2 again). If no batches left → start next iteration (Phase 1 again) or **start Phase 5 if there is an open PR for the trunk/cluster branches**.

### Phase 4 stack-depth cap

Hard cap: any single dependency stack ≥ 5 PRs deep triggers a controller halt. Reason: rebase blast-radius compounds — reviewer changes to the bottom PR force-rebase the entire stack, and reviewers stop landing PRs that get rebased twice. On cap:
- send PushNotification with the stack contents,
- merge all completed lower PRs into `integration_branch` immediately (collapse stack to a single base),
- continue remaining clusters from the collapsed base.

---

## Phase 5 — Remote CI watch (controller, after push)

Local CI passing is necessary but not sufficient. Remote CI runs additional jobs that don't fit on the controller machine (kafka integration, projection provider e2e, host composition smoke, codecov, etc.). Phase 5 watches them and treats remote failures the same way Phase 3 treats verify failures: dispatch a focused fix codex, loop back through verify/merge.

### When Phase 5 fires

After every push to `<trunk_branch>` that is the head of an open PR. Detect open PR with:

```bash
PR_NUMBER=$(gh pr list --head "<trunk_branch>" --json number --jq '.[0].number')
```

If no open PR → skip Phase 5 (local CI is sufficient).

### Arm the watch

```bash
# Poll every 60s; emit one event per failed check; exit when all checks settled.
prev=""
while true; do
  state=$(gh pr checks "$PR_NUMBER" --json name,bucket,state)
  cur=$(jq -r '.[] | "\(.name)\t\(.bucket)\t\(.state)"' <<<"$state" | sort)
  comm -13 <(printf '%s\n' "$prev") <(printf '%s\n' "$cur") | awk -F'\t' '$2=="fail"{print $0}'
  prev=$cur
  if jq -e 'all(.bucket != "pending")' <<<"$state" >/dev/null; then
    failed=$(jq -r '[.[] | select(.bucket=="fail") | .name] | length' <<<"$state")
    echo "REMOTE_CI_DONE:failed=$failed"
    break
  fi
  sleep 60
done
```

Arm as a Monitor with `persistent: true`. Each emitted line is a notification you wake on. Stop only on the `REMOTE_CI_DONE:` line.

### Triage on failure

For each `bucket: fail` check:

1. Fetch the failure logs:
   ```bash
   RUN_URL=$(gh pr checks "$PR_NUMBER" --json name,link --jq '.[] | select(.name=="<check>") | .link')
   RUN_ID=$(basename "$(dirname "$RUN_URL")")  # parse from link
   gh run view "$RUN_ID" --log-failed > .refactor-loop/logs/remote-ci-<check>-<sha>.log 2>&1 || \
     gh run view "$RUN_ID" --log | tail -200 > .refactor-loop/logs/remote-ci-<check>-<sha>.log
   ```

2. Classify:
   - **Flaky / infra-only** (network timeout, registry unreachable, runner OOM that doesn't recur): retry by `gh workflow run` or pushing an empty whitespace commit; document under `clusters_failed` with reason `flaky`.
   - **Real failure tied to merged work**: dispatch a `prompts/remote-ci-fix.md` codex (see template) with the failure log + last 10 cluster commits as input. Treat the resulting fix as a mini-cluster: implement → controller verify (re-run local guards + the specific failing test) → commit → push → Phase 5 again.
   - **Pre-existing failure unrelated to merged work** (failure exists on `dev` base too): document, do not fix in this PR; surface via PushNotification.

3. `codecov/patch` specifically: this measures coverage on **lines added by this PR**, i.e. the refactor's own new/modified production lines. A refactor-induced patch-coverage drop is the loop's own responsibility — the loop just shipped new code without tests, that is exactly what the loop must close before merge. Treat as a **real failure**:
   - Pull the codecov patch detail via API (`https://api.codecov.io/api/v2/github/<owner>/repos/<repo>/pulls/<num>`) to identify `patch.misses` + `patch.partials` line ranges per file.
   - Cross-reference with the cluster ledger: each uncovered patch line belongs to a known cluster.
   - Dispatch `prompts/test-add.md` codex per cluster with the uncovered file:line list, target threshold (default 80% patch coverage), and "tests must exercise behavior the cluster introduced (e.g., IHttpClientFactory typed-client path, head-index cursor compaction trigger, compiled-delegate exception path, projection session lease lifecycle)".
   - Test-add codex output joins the cluster's branch and re-pushes; codecov re-evaluates.
   - **Exception** (info-only ack): if `head_totals.coverage - base_totals.coverage > -0.5%` (i.e. project coverage barely moved) AND the cluster summary explicitly declared deletion-heavy refactor, you may ack the codecov failure with a PushNotification explaining the math; do not silently dismiss.

### Loop control under Phase 5

- Cap remote-ci fix attempts per check at **2**. After 2 attempts on the same check → mark `clusters_failed` reason `remote-ci-stuck`, send PushNotification, stop the loop.
- Phase 5 may overlap with Phase 2 of the next iteration. If a new cluster's local CI passes but remote CI is still failing on a prior commit → push anyway (CI re-runs on each push); the watch picks up the latest checks.

---

## Phase 6 — Design-issue watch (sweep on every wakeup)

Runs **before any other phase work** on every controller wakeup (whether triggered by user `/loop`, ScheduleWakeup, or task-notification). Goal: detect when a paused-for-design cluster has a maintainer response and resume it.

### Sweep procedure

For each `state.design_pending[i]`:

```bash
issue_json=$(gh issue view "$ISSUE_NUMBER" --json comments,state,labels)
new_count=$(jq -r '.comments | length' <<<"$issue_json")
prev_count=$LAST_COMMENT_COUNT   # from state
state=$(jq -r '.state' <<<"$issue_json")
labels=$(jq -r '[.labels[].name] | join(",")' <<<"$issue_json")
```

Classify:

- **No new comments AND state==open**: nothing to do; bump `last_checked` only.
- **State==closed without `auto-loop-resume` label**: maintainer closed without resume signal. Move to `clusters_failed` with reason `design-rejected:closed`. PushNotification: "cluster-<id> design issue #<num> closed without auto-resume; cluster permanently deferred."
- **New comment(s) AND no `auto-loop-resume` label**: maintainer is discussing. PushNotification: "cluster-<id> design issue #<num> has N new comment(s) — manual review recommended" (only the first time a new comment appears; do not re-notify each sweep).
- **Label `auto-loop-resume` is set** (maintainer's explicit green light): controller resumes:
  - Extract the latest comment body (assumed to contain the design decision: chosen pattern, proto schema, scope adjustments).
  - Materialize a new `prompts/implement-<cluster-id>.md` that prepends the design decision verbatim under a `## Design decision (from issue #<num>)` heading, then proceeds with the regular implement instructions.
  - Move cluster from `design_pending` into `clusters_active` and dispatch as a normal Phase 2 implement.
  - Post a comment back on the issue: "auto-loop resumed; implement codex dispatched. Will close after PR opens."

Update `state.design_pending[i].last_comment_count` and `last_checked` after every sweep, regardless of outcome.

### Sweep cadence

- Every controller wakeup runs the sweep (cheap: `gh issue view` per pending cluster, typically ≤5 pending).
- If there are pending design issues and no other active phase work, set ScheduleWakeup to **3600s** (1h) as the design-poll cadence — issues don't usually get responses minute-to-minute.
- Stop the loop entirely (omit ScheduleWakeup, PushNotification stop) only when **no design_pending AND no clusters_active AND no rollup_pr awaiting CI**. Otherwise the loop must keep heartbeating to catch design responses.

### Manual override

If the user manually edits state.json and sets `design_pending[i].status = "resume"`, the next sweep treats it as if `auto-loop-resume` label was applied (escape hatch when label can't be set on the host).

---

## Loop control

- **Stop conditions**: all planned clusters done OR every remaining cluster failed twice.
- **Stop action**: omit ScheduleWakeup, TaskStop any monitor, send one-line PushNotification with summary.
- **Wakeup cadence**:
  - Primary: harness task notifications (auto on codex exit).
  - Fallback: 1200–1800s ScheduleWakeup (matches /loop dynamic mode guidance).

---

## Hard rules (controller-level, propagated into every codex prompt)

1. **No new features** — only clean violations of CLAUDE.md philosophy.
2. **No external repo changes** — NyxID / chrono-* are out of scope.
3. **Code self-documents the refactor** — every refactored type/method gets a 3-5 line comment of the form `// Refactor (iterN/cluster-XXX): Old pattern: …  New principle: …`.
4. **No `commit`/`push`/`checkout` inside codex prompts** — the controller owns git topology.
5. **No `Task.Delay`-based test pacing** — tests must use deterministic awaiters.
6. **No `[Skip]` / disabled tests** as a way to make CI green.
7. **No scope creep** — codex must print `SCOPE_EXTEND: <file> <reason>` before touching anything outside `scope_paths`.

---

## Files

- [prompts/audit.md](prompts/audit.md) — audit phase template
- [prompts/implement.md](prompts/implement.md) — implement phase template (per cluster)
- [prompts/verify.md](prompts/verify.md) — verify phase template (per cluster)
- [prompts/remote-ci-fix.md](prompts/remote-ci-fix.md) — Phase 5 remote-CI fix template
- [prompts/test-add.md](prompts/test-add.md) — Phase 5 codecov-driven test-add template (per cluster)
- [prompts/design-issue-body.md](prompts/design-issue-body.md) — Phase 1/6 GitHub issue body for `requires_design: true` clusters
- [scripts/spawn-codex.sh](scripts/spawn-codex.sh) — standardized `codex exec` wrapper (enforces 3600s minimum timeout)
- [REFERENCE.md](REFERENCE.md) — state schema, batching heuristics, recovery playbook
