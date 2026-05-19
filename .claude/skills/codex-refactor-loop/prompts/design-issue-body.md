# Auto-refactor loop: design decision needed for `${CLUSTER_ID}`

This issue was opened automatically by the `codex-refactor-loop` skill during iter${ITERATION}. The loop's audit codex identified a real architectural violation but flagged it `requires_design: true` because the fix is not a mechanical refactor — it needs a human design decision before any code change.

The loop has **paused** on this cluster. Auto-implementation will resume only when this issue is either:

- **Closed with the `auto-loop-resume` label** (signals "design rejected; do not implement this cluster").
- **Labelled `auto-loop-resume`** with a comment containing the design decision (signals "implement using this design").

Without one of those signals, the controller polls this issue every ~1 hour and surfaces new comments via PushNotification.

---

## Cluster spec (from `.refactor-loop/runs/audit-iter-${ITERATION}.md`)

${CLUSTER_YAML}

## Evidence

${CLUSTER_EVIDENCE}

## Fix boundary (audit's initial proposal)

${CLUSTER_FIX_BOUNDARY}

---

## Decision checklist

Please answer at least these before adding the `auto-loop-resume` label:

- [ ] **Pattern choice**: which of the audit's proposed fix shapes (or an alternative) should the implement codex use?
- [ ] **Proto schema impact**: if new typed fields are needed, sketch them here (proto messages + field numbers). If no proto change, say so.
- [ ] **Backward compatibility**: how should existing persisted state / wire format be handled? (Reserve, alias, drop with reset, etc.)
- [ ] **Scope split**: should this be one cluster or split into N PRs? If split, sketch the cluster ids.
- [ ] **Test surface**: what behavior MUST be exercised by tests beyond the audit's `verification_hints`?
- [ ] **Out-of-scope guard rails**: anything the implement codex must NOT touch (e.g., a related concern that's a separate issue)?

## Auto-loop behavior

- Controller polls this issue on every wakeup (~1h cadence when no other work is active).
- First new comment after issue open → PushNotification to controller operator.
- `auto-loop-resume` label → controller materializes implement prompt with this issue's latest comment prepended verbatim as `## Design decision (from issue #${ISSUE_NUMBER})`, dispatches implement codex, posts confirmation back on this issue, and closes after PR opens.
- Issue closed without `auto-loop-resume` label → controller treats as "design rejected; cluster permanently deferred".

cc: @auric (auto-loop operator)
