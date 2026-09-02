# Workflow Activity vNext Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver every approved Workflow Activity vNext user path under the isolated scoped route namespace using only authoritative backend data and existing authentication/localization behavior.

**Architecture:** The route package owns page composition and transient interaction state. Existing Studio, scope, runtime, auth, and Settings adapters remain authoritative; one new typed adapter owns observatory and fork transport decoding. TanStack Query keys include the route scope and normalized filters, while accepted-to-readable and accepted-to-observed transitions use explicit receipt-bound state machines.

**Tech Stack:** React 19, TypeScript, Umi Max, Ant Design, TanStack Query, XYFlow/GraphCanvas, Jest, Testing Library, Biome.

---

### Task 1: Isolated routes and typed transport boundary

**Files:**
- Modify: `config/routes.ts`
- Modify: `src/routesConfig.test.ts`
- Create: `src/shared/models/workflowActivity.ts`
- Create: `src/shared/api/workflowActivityApi.ts`
- Create: `src/shared/api/workflowActivityApi.test.ts`

- [ ] Write route assertions for all seven hidden vNext routes, literal `workflows/new` ordering, namespace-only redirect, and unchanged legacy redirects; run the single route test and confirm RED.
- [ ] Add precise observatory summary/detail/graph, filter, and fork receipt models plus decoders that preserve unknown status values and reject malformed required identity/version fields.
- [ ] Write adapter tests for encoded scope/filter queries, independent detail/graph requests, 401/403/404 propagation, fork request identity, and receipt decoding; run and confirm RED before implementation.
- [ ] Implement the seven route records and typed adapter, then rerun only `src/routesConfig.test.ts` and `src/shared/api/workflowActivityApi.test.ts` to GREEN.

### Task 2: Scoped shell, catalogue, and direct creation

**Files:**
- Create: `src/pages/workflow-activity-vnext/index.tsx`
- Create: `src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Create: `src/pages/workflow-activity-vnext/navigation.ts`
- Create: `src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Create: `src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
- Create: `src/pages/workflow-activity-vnext/workflows/workflowCreation.ts`
- Create: `src/pages/workflow-activity-vnext/hooks/useDraftMaterialization.ts`
- Create: `src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Write route integration tests for authoritative catalogue loading, successful empty data, partial-source failure, search, and real-ID navigation; run the new page test and confirm RED.
- [ ] Write creation tests for Describe, blank, YAML import, and bundled versioned template paths. Cover materialized and `202 projection_pending` responses, bounded `404`, preserved receipt/input, retrying the same GET, and duplicate-submit prevention; confirm RED.
- [ ] Implement the local rail/mobile navigation with existing `ConsoleLanguageSwitch` and `ConsoleAuthActions`, two-source catalogue, four direct creation forms, and receipt-bound materialization hook.
- [ ] Rerun only the new route test after each user-path slice until UP-00 through UP-05 are GREEN.

### Task 3: Common editor, first save, and draft execution

**Files:**
- Create: `src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Create: `src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Create: `src/pages/workflow-activity-vnext/hooks/useDraftRun.ts`
- Reuse: `src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.tsx`
- Reuse: `src/pages/team-member-workflow-studio/components/WorkflowStudioNodeLibrary.tsx`
- Reuse: `src/pages/team-member-workflow-studio/components/WorkflowStudioNodeDetailPanel.tsx`
- Reuse: `src/pages/team-member-workflow-studio/components/WorkflowStudioYamlPanel.tsx`
- Reuse: `src/pages/team-member-workflow-studio/components/WorkflowStudioDraftRunPanel.tsx`

- [ ] Write editor tests for exact draft loading, committed fallback, dirty state, YAML/canvas shared state, validation findings, existing-draft PUT, and committed-only first-save POST with returned-ID route replacement; confirm RED.
- [ ] Implement the editor hook and page using existing Studio graph/document helpers without member APIs or member identity.
- [ ] Write draft-run tests for real serialized YAML, stable submission, accepted/running state, disconnected/error state, no Activity completion claim, and general Open Activity when no trustworthy run ID exists; confirm RED.
- [ ] Implement `runtimeRunsApi.streamDraftRun` integration and authoritative stream presentation, then rerun the route test to GREEN for UP-06 and UP-07.

### Task 4: Activity observation, ledger, detail, and recovery

**Files:**
- Create: `src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Create: `src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Create: `src/pages/workflow-activity-vnext/activity/runRecovery.ts`
- Create: `src/pages/workflow-activity-vnext/hooks/useRunObservation.ts`

- [ ] Write tests for URL-backed server filters, workflow definition resolution, filter-unavailable fallback, loading/empty/error/unknown statuses, and recent-window wording; confirm RED.
- [ ] Implement the Activity query and ledger without name joins, local totals, revisions, duration, usage, outcome, or Needs-you filtering.
- [ ] Write tests for independent detail/graph loading, safe not-found, partial running detail, graph-only failure, failed-step eligibility, first-executable-step eligibility, immutable source, fork receipt display, and no `newRunActorId` detail navigation; confirm RED.
- [ ] Implement detail, graph, Retry, Run again, and bounded observation, then rerun the route test to GREEN for UP-08 through UP-12.

### Task 5: Settings, identity, responsive behavior, and locale

**Files:**
- Create: `src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`
- Create: `src/pages/workflow-activity-vnext/styles.ts`
- Modify: `src/locales/en-US.ts`
- Modify: `src/locales/zh-CN.ts`
- Modify: `src/locales/projectMessages.en-US.ts`
- Modify: `src/locales/projectMessages.zh-CN.ts`
- Modify: `src/locales/catalog.test.ts`

- [ ] Write tests for real LLM loading/dirty/accepted/observed/catalogue-unavailable/save-failure states, auth-me identity/expiry, existing auth actions, runtime loading/unavailable, and no localStorage authority; confirm RED.
- [ ] Implement AI Defaults by reusing `userLlmSelection.ts` and `observeUserLlmSave`, Account through existing session/actions, and Advanced through `getUserConfigRuntime`.
- [ ] Add every new message to both locale catalogues and verify catalogue parity with the single locale test.
- [ ] Add responsive Operational Automation Ledger styles and test keyboard-visible controls, semantic status, dialogs, long identities, and mobile section navigation; rerun only the new page and locale tests to GREEN for UP-13 through UP-16.

### Task 6: Focused verification and delivery

**Files:**
- Review all files changed relative to `origin/feat/2026-08-04_workflow-activity-vnext`

- [ ] Run `frontend_change_scope.py --base origin/feat/2026-08-04_workflow-activity-vnext`, explicitly execute every changed/new Jest file and dependency-related tests, then run Biome only on reported static-check files.
- [ ] Because the user explicitly requested full TypeScript and production-build validation, run the package `tsc` and `build` with `CODEX_ALLOW_FULL_FRONTEND_VALIDATION=1`; do not run the complete Jest suite.
- [ ] Run the design baseline verifier and confirm the declared SHA, byte-identical generator output, and 17/17 frames.
- [ ] After the environment synchronizer succeeds, verify real authenticated desktop/tablet/mobile routes in-browser and capture screenshots. If the synchronizer/backend/OAuth environment remains unavailable, record the exact gap without mocks.
- [ ] Review the complete frontend-only diff, stage only this task's files, commit with an imperative message, push, and create a Draft implementation PR without modifying PR #3187 or enabling auto-merge.
