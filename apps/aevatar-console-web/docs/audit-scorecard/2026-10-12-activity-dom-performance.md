# Activity Run detail DOM update boundaries

## Evidence and scope

The Activity detail Performance capture shows time in Layout, Recalculate
Style and DOM removal. This is evidence to investigate rendering work, not
an attribution of every task to one source function or a before/after timing
benchmark. The earlier 55–70 ms Long Task samples came from a local dev build;
production timing still needs a comparable production-build recording.

The installed Ant Design Typography implementation measures plain single-line
`ellipsis` by appending an element, reading bounding rectangles and removing
that element. The Activity detail uses these labels throughout its history
rail and steps. These labels do not need expansion or a measured tooltip, so
CSS truncation removes this source of DOM writes and layout reads. Typography
styling, full text and native titles remain available.

## Implementation and recurrence prevention

- `RunDetailPage` and `RunHistoryRail` use a scoped CSS class for single-line
  truncation. Keep measured ellipsis for controls that actually need its
  behavior; do not turn it back on for these plain labels.
- The history rail is memoized and receives stable filtered arrays and a
  stable navigation callback. Selecting a step no longer reconstructs the
  unrelated history list. Existing loading, error and navigation semantics
  remain owned by the page and its queries.
- `RunDetailInspectorTabs` constructs Input and Timeline contents only after
  their tab mounts. Visited hidden panes retain their committed DOM and skip
  updates for new run/step props. Revealing a pane reconciles the latest props;
  it does not display a snapshot from a previously selected step. Tabs are
  not destroyed when hidden. A timeline with no matching step events still
  falls back to the full returned run timeline.
- Execution graph edges use a collision-safe source/target/branch tuple and
  canonical ordering. Equivalent API edge reordering therefore preserves
  reconciled references and the canvas topology signature. Status changes
  still update the affected graph elements.

The nearest regression coverage protects reordered/collision-prone edges,
fresh Input and Timeline content after hiding/reopening them, and the existing
history/graph continuity behavior. No backend, API, route, locale catalogue,
shared graph canvas or dependency change is required.

## Verification

Run from the repository root:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/executionGraph.test.ts
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web exec biome check src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx src/pages/workflow-activity-vnext/activity/RunHistoryRail.tsx src/pages/workflow-activity-vnext/activity/RunDetailInspectorTabs.tsx src/pages/workflow-activity-vnext/activity/executionGraph.ts src/pages/workflow-activity-vnext/activity/executionGraph.test.ts src/pages/workflow-activity-vnext/styles.ts
pnpm --dir apps/aevatar-console-web build
pnpm --dir apps/aevatar-console-web build:stats
pnpm --dir apps/aevatar-console-web check:workflow-route-bundle
bash tools/ci/test_stability_guards.sh
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
git diff --check
```

The graph file contains four pure-function unit cases; its existing Jest
project uses jsdom, but these cases do not render or integrate components.
Local verification follows the machine's incremental-unit-only policy.
`RunDetailPage.test.tsx` contains the two added interaction regressions for CI;
component/route integration, browser smoke and the complete frontend suite
are not run locally. Existing CI owns complete frontend verification.

The bundle guard requires the stats build first. It checks loading boundaries,
not browser timing. No millisecond, FPS, FCP or LCP improvement is claimed.
For a later timing comparison, use the same production build mode, data,
viewport and throttling and record initial entry, step selection, tab reveal
and refresh separately. Check long unbroken labels and actual scroll
containers in the browser; jsdom cannot establish layout performance.

## Design-baseline declaration

- Baseline: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/`.
- Primary design: `aevatar-workflow-activity-vnext.excalidraw`.
- SHA-256: `30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
- Contract: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`.
- User paths: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`, especially UP-10 and UP-16.
- Authentication and localization: existing login, callback, session, returnTo
  and Umi locale behavior. Memoized history and tab labels subscribe to the
  existing locale context.
- Production data: real APIs and API-acknowledged actions only; no mock fallback.
- Baseline integrity: the verifier command above checks hashes and generated
  artifacts. The baseline assets remain unchanged.
