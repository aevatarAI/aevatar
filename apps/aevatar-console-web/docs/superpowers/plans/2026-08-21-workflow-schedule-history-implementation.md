# Workflow Schedule Management and History Implementation Plan

> Execute this plan with `superpowers:executing-plans`, `superpowers:test-driven-development`, and `frontend-incremental-pr`.

**Goal:** Replace the flat Schedule detail surface with the approved List -> Overview/History -> Edit state model and expose Schedule-origin filter context in Activity without changing backend contracts.

**Architecture:** Keep `WorkflowScheduleSurface` as the single Workflow-scoped Schedule owner. The existing Schedule detail query remains the authoritative source for both Overview and bounded recent attempts, while Activity remains the only owner of actual Workflow Runs. UI state distinguishes the selected Schedule, active detail tab, and temporary edit mode; it never derives a Run identity from Schedule attempt metadata.

**Stack:** React, TypeScript, Ant Design, TanStack Query, Jest, Testing Library, Umi history/i18n.

---

## Task 1: Approve the baseline and establish the selected-Schedule shell

**Files:**
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-21-workflow-schedule-history-design.md`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`

1. Change the focused specification status from awaiting review to approved.
2. Replace the legacy flat-detail test with a failing test proving that selecting a Schedule renders its name and owning Workflow in the stable header, defaults to Overview, hides History content until selected, and returns to the list through the header Back control.
3. Run only the named Jest test and confirm it fails for the missing approved shell.
4. Add an `overview | history` tab state that resets to Overview whenever a Schedule is newly selected; keep Edit as the existing temporary form mode.
5. Render a stable selected-Schedule title with header Back and the container's existing Close control. Remove the footer Back action.
6. Re-run the named test and confirm it passes.

## Task 2: Implement the Overview information hierarchy and actions

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`

1. Add a failing test for the approved Overview order and optional-data rules: status, human recurrence, timezone, next scheduled time, last attempt, counts, non-empty Run input, and collapsed raw cron.
2. In the same test, prove Run now and Edit schedule are direct actions while Pause/Enable and Delete schedule live in More; confirm Delete opens a confirmation naming the Schedule.
3. Run the named test and confirm the expected failure.
4. Extract reusable recurrence and application-locale date presentation helpers. Format dates with `getLocale()` and the Schedule timezone, with a safe fallback for invalid timezone values.
5. Refactor the collection rows to show human recurrence, timezone, and next scheduled time, with the row as the only selection target.
6. Render the Overview facts and action hierarchy. Keep raw cron in collapsed Advanced details and omit empty Run input.
7. Use accepted-operation Toast feedback without implementation narration. Keep authoritative data refresh through the existing Workflow-scoped query prefix.
8. Re-run the named test and confirm it passes.

## Task 3: Implement recent-attempt History and Activity handoff

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`

1. Add a failing test that opens History and expects the compact columns Scheduled time, Source, Result, and Completed time.
2. Assert scheduled/manual and succeeded/failed mappings, generic failed-attempt copy, raw error only under Technical details, no guessed per-attempt Run link, and the empty `No attempts yet` state.
3. Assert `View related runs in Activity` closes Schedule management and pushes exactly `workflowId`, `schedule`, and `origin=schedule`.
4. Run the named test and confirm the expected failure.
5. Implement the History table from `recentFires` without deriving new categories or identities.
6. Add tab-specific request failure copy and Retry while preserving the selected Schedule and active tab.
7. Implement the Activity handoff through the shared history adapter.
8. Re-run the named test and confirm it passes.

## Task 4: Make Activity filter context visible and removable

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`

1. Add a failing test for a Schedule-origin Activity URL that proves the Workflow, Schedule, and Source filters are all visible and the API receives the existing typed filters.
2. Assert that each visible context control removes only its owned query parameter.
3. Run the named test and confirm the expected failure.
4. Extend the existing filter-context strip with Schedule and human-readable Source controls; reuse the current URL replacement path and do not add a new API.
5. Re-run the named test and confirm it passes.

## Task 5: Align durable copy and styling

**Files:**
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

1. Add or update locale messages for Overview, History, attempt source/result, failure summaries, More actions, Activity filter context, and accepted Toasts in both locales.
2. Replace legacy Schedule detail/fire styles with the approved compact tabbed layout, stable fact grid, action hierarchy, table, disclosure, and responsive behavior.
3. Run the full `WorkflowScheduleSurface.test.tsx` and `ActivityPage.test.tsx` files only.
4. Fix only failures attributable to these changed behaviors and rerun the same focused files.

## Task 6: Focused validation and PR delivery

**Files:**
- Review all files changed by Tasks 1-5.
- Keep `.planning/` untracked and unstaged.

1. Run the frontend scope analyzer with `--base origin/feat/2026-08-04_workflow-activity-vnext`.
2. Run every analyzer-selected related test plus the two explicitly changed test files; do not run the full frontend suite.
3. Run Biome only on analyzer `staticCheckFiles`. Do not run full `tsc` or a production build; delegate unavailable affected typechecking and the full suite/build to GitHub CI.
4. Run `bash tools/ci/test_stability_guards.sh` because frontend tests changed.
5. Run documentation lint for the modified specification and `git diff --check`.
6. Review the complete base diff for semantic drift, unrelated changes, leaked backend identifiers, false Run claims, placeholders, and responsive/accessibility regressions.
7. Stage only current task files, commit with an imperative focused message, push `feat/2026-08-20_workflow-schedule-frontend`, and update Draft PR #3498.
8. Preserve `[DO NOT MERGE]`, Draft status, and base `feat/2026-08-04_workflow-activity-vnext`; record exact focused commands and state that full frontend verification is delegated to GitHub CI.

## Implementation Status

- Tasks 1–5: completed. The selected-Schedule shell, Overview, bounded History,
  Activity handoff, locale copy, and responsive styles are implemented.
- Task 6 focused tests: passed for `WorkflowScheduleSurface.test.tsx`,
  `ActivityPage.test.tsx`, `index.test.tsx`, and `workflowScheduleApi.test.ts`.
- Task 6 static checks: Biome, test stability guards, documentation lint, and
  `git diff --check` passed. Full frontend typecheck, suite, and production
  build remain delegated to GitHub CI under the incremental PR policy.
