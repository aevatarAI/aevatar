# codex-refactor-loop — Reference

Detailed specifications, edge cases, and recovery playbook. The main workflow is in [SKILL.md](SKILL.md).

## State schema (`.refactor-loop/state.json`)

```json
{
  "schema_version": 1,
  "loop_started_at": "<ISO8601>",
  "trunk_branch": "<branch the loop integrates into>",
  "max_parallel_clusters": 3,
  "iteration": 1,
  "phase": "audit | implement-batch-X | verify-batch-X | merge | done",
  "audit": {
    "status": "running | done | failed",
    "log": "<relative path>",
    "output": "<relative path>",
    "total_clusters": <int>
  },
  "clusters_planned": [
    {"id": "cluster-001", "batch": "A", "risk": "low|medium|high", "leverage": "low|medium|high"}
  ],
  "clusters_active": [
    {
      "id": "cluster-001",
      "phase": "implement | verify",
      "worktree": "<relative path>",
      "branch": "<refactor/iterN-cluster-id>",
      "bg_task": "<harness background task id>",
      "log": "<relative path>"
    }
  ],
  "clusters_done": [
    {"id": "cluster-001", "merged_at": "<ISO8601>", "commit": "<sha>"}
  ],
  "clusters_failed": [
    {"id": "cluster-001", "phase": "implement|verify|merge", "reason": "<short>"}
  ]
}
```

## Batching heuristics (Phase 1 → Phase 2 transition)

Goal: parallel safety. Two clusters can be in the same batch **only if** all four hold:

1. `scope_paths` file overlap = 0.
2. They touch different `.csproj` files (compile-time isolation).
3. They touch different proto files.
4. Their `dependencies:` lists don't reference each other.

Greedy bin-packing:

1. Sort `clusters_planned` by `risk` (low first), then `leverage` (high first).
2. For each cluster, assign to first batch where it's compatible with every existing member.
3. Each batch has at most `max_parallel_clusters`.

If a cluster cannot fit in any new batch ≤ `max_parallel_clusters`, start a new batch for it.

## Recovery playbook

### Audit codex crashed / timed out

- Log will end with `EXIT=124` (timeout) or non-zero (crash).
- Re-dispatch with narrower scope: split scan into two passes (write a smaller audit prompt focused on a sub-area).

### Implement codex returned `partial` or `blocked`

- Read the cluster's implement summary for blocker description.
- If blocker is "scope ambiguity" → tighten prompt, re-dispatch.
- If blocker is "test fundamentally broken" → spawn a separate "fix the test" mini-cluster before retrying.
- After 2 consecutive failures → move to `clusters_failed`, do NOT auto-retry; surface via PushNotification.

### Verify returned `rework`

- Append verify's "Rework instructions" section to the cluster's implement prompt.
- Re-dispatch implement codex in the same worktree (do not destroy the worktree; codex keeps the working tree changes plus rework instructions).
- After 2 rework cycles → escalate to `abort`.

### Merge conflict in Phase 4

- `git merge --abort` first.
- Treat as `rework` with conflict diff appended to the prompt.
- Re-dispatch implement codex with explicit instruction: "rebase your changes onto trunk HEAD, resolve listed conflicts".

### Trunk branch moved while batch was in flight

- Detect via `git rev-parse HEAD` vs `state.json.trunk_head` before each merge.
- If moved → for each `pass` cluster: rebase its branch onto new trunk HEAD inside its worktree, re-run verify, then merge.

## Loop termination

Stop when:

- `clusters_planned == clusters_done ∪ clusters_failed`, AND
- no `clusters_active` remain, AND
- user did not request a new iteration.

On stop:

- Send a one-line PushNotification: `refactor loop done: N merged, M failed`.
- Leave `.refactor-loop/` intact for post-mortem.
- Omit ScheduleWakeup.

## Iteration > 1

If after stop the user re-invokes the loop:

- Increment `iteration` in state.json.
- Re-run Phase 1 audit. Audit prompt should naturally skip already-fixed violations (they no longer match `rg`).
- Continue normally.

## Comparison to `refactor-team` skill

| Aspect | `refactor-team` | `codex-refactor-loop` (this skill) |
|---|---|---|
| Implementer | Claude Agent subagents | `codex exec` subprocesses |
| Parallelism | In-process Agent calls | OS-level via worktrees |
| Pacing | One cycle per `/refactor-team` invocation | Continuous `/loop` dynamic wakeups |
| State | Implicit in subagent prompts | Explicit `.refactor-loop/state.json` |
| Merge | Auditor commits to integration branch | Controller merges per cluster |

Use `refactor-team` when you want one human-supervised cycle. Use `codex-refactor-loop` when you want true unattended multi-hour parallel refactor.
