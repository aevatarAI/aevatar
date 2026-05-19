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
  "trunk_branch": "<current branch>",
  "integration_branch": "<current branch>",
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
     --timeout 2400
   ```

   Use Bash with `run_in_background: true`. 2400s (40 min) accommodates the mandatory coverage manifest + opened-file requirements.

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
- A cluster with `requires_design: true` (audit-marked deep violation) goes in a batch by itself; controller may also escalate it to a separate planning issue rather than auto-implement.

Update state, advance to Phase 2.

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

3. `codecov/patch` specifically: this is a coverage delta check, not a test failure. Treat as **info-only** by default unless the user has explicitly required coverage gates on this branch. Document in scorecard, don't auto-fix.

### Loop control under Phase 5

- Cap remote-ci fix attempts per check at **2**. After 2 attempts on the same check → mark `clusters_failed` reason `remote-ci-stuck`, send PushNotification, stop the loop.
- Phase 5 may overlap with Phase 2 of the next iteration. If a new cluster's local CI passes but remote CI is still failing on a prior commit → push anyway (CI re-runs on each push); the watch picks up the latest checks.

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
- [scripts/spawn-codex.sh](scripts/spawn-codex.sh) — standardized `codex exec` wrapper
- [REFERENCE.md](REFERENCE.md) — state schema, batching heuristics, recovery playbook
