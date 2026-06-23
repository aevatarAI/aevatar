---
title: "Team Workflow Realtime Visibility Implementation Plan"
status: pending-confirmation
owner: codex
created: 2026-06-22
source_prd: docs/designs/2026-06-22-team-workflow-realtime-visibility-prd.md
branch: docs/2026-06-22_workflow-realtime-visibility-prd
---

# Team Workflow Realtime Visibility Implementation Plan

This plan implements the agreed PRD:

- [Team Workflow Realtime Visibility PRD](2026-06-22-team-workflow-realtime-visibility-prd.md)

No code should be changed until this plan is confirmed.

## 1. Semantic Brief

### Goal

Make Team workflow execution visible through a Team Run Cockpit: Team detail can show what Team-entry workflow work is happening, and a run cockpit can reopen a run through a durable owner tuple rather than route guesses.

### Current Meaning

The codebase has several partial surfaces:

- Team/member invoke can stream AGUI frames while a run is active.
- Member-scoped run endpoints expose run summaries and audit artifacts.
- Workflow Studio execution UI can parse run frames.
- Team detail has member/test/runtime areas, but no stable Team-owned run cockpit.

The current product can easily imply "Team latest work" while actually looking at "entry member latest run." That is unsafe unless the run has durable Team invocation context.

### Target Meaning

Team is the product context. Runtime execution is owned by the accepted run's immutable owner tuple:

```text
scopeId + teamId context + ownerMemberId + publishedServiceId + runId
```

The cockpit must resolve this tuple before calling member-scoped run endpoints. Live frames are session evidence only. Durable run summary and audit are the source of truth.

### Semantic Decision

V1 Core is a read-only durable cockpit with live enhancement:

- Team detail may say **Team current work** only when run data has durable `invocationSource = "team"` and matching `teamId`.
- Without Team attribution, Team detail must say **Entry member latest run**.
- Historical `publishedServiceId` is captured at acceptance time and must not be recomputed from the member's current binding.
- Waiting rows do not render actions in V1 Core unless typed capabilities already exist.
- Evidence copy must be redaction-safe; unknown sensitivity excludes raw payload bodies.

### Action Safety / Recovery

- Accepted run receipt means dispatch accepted, not completed.
- Accepted-but-not-materialized is a first-class state: `accepted / durable pending`.
- Missing audit is not failure; show `audit unavailable`.
- Live stream disconnected is source state, not run status.
- User-triggered controls are out of V1 Core unless typed capabilities exist.
- No localStorage, browser recent-runs, process-local dictionary, or query-time replay can become authority.

### Style Direction

Dense, calm, operational, and scan-friendly:

- Timeline is the default view.
- Evidence is a drill-down layer, not the first screen.
- Graph/map renders only when authoritative run layout exists; otherwise use ordered timeline.
- Team context comes first; owner member and runtime identities are visible but secondary.

## 2. Plan

| Step | Agent | Goal | Scope | Risk | Validation | Done Criteria |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `architecture-explorer` | Confirm current backend contracts and exact owner resolver landing point. | Read-only backend/API/tests. | Medium | Code inspection only. | Decision note for owner resolver, accepted envelope, and test targets. |
| 2 | `logic-worker` | Add minimal durable owner resolver and accepted Team run owner envelope. | Backend endpoints/contracts/tests. | High | Targeted `dotnet test`; relevant guards if query/projection touched. | `teamId + runId` cold-start resolves immutable owner tuple; accepted Team run can pin cockpit. |
| 3 | `logic-worker` | Add frontend route/API/navigation foundation. | Frontend routes, navigation helpers, API clients, tests. | Medium | `pnpm --dir apps/aevatar-console-web tsc`; targeted route/API tests. | `/scopes/:scopeId/teams/:teamId/runs/:runId` builds/parses; owner tuple is resolved before member queries. |
| 4 | `logic-worker` | Build durable-first cockpit view model. | Frontend run/cockpit model modules and tests. | Medium | Focused model tests. | Durable data wins over live frames; source status differs from run status; redaction-safe copy model exists. |
| 5 | `style-worker` | Implement Team detail strip and read-only cockpit UI. | Team pages/components/locales/tests. | Medium | Frontend tests; browser visual check when app can run. | Team strip labels are honest; cockpit has Timeline, Information, Output, Evidence; no V1 waiting actions without typed capabilities. |
| 6 | `verifier` | Run final validation and semantic audit. | Read-only touched diff and test suite. | Medium | Guards, targeted backend tests, frontend type/test checks. | Identity boundaries, authority rules, and PRD acceptance criteria are verified. |

## 3. Work Units

### 3.1 Architecture Exploration

```yaml
id: architecture-contract-map
title: Map accepted run, owner resolver, and current run query contracts
agent_role: architecture-explorer
semantic_decision: Team context is not runtime owner; historical run inspection requires immutable owner tuple.
action_safety: Accepted receipt is not completion; missing durable read model is pending, not failure.
scope_paths:
  - src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs
  - src/Aevatar.Studio.Application/Studio/Services/StudioTeamGAgentStreamInvocationService.cs
  - src/Aevatar.Studio.Application/Studio/Services/StudioTeamEntryMemberResolver.cs
  - src/Aevatar.Studio.Application/Studio/Services/StudioMemberService.cs
  - src/Aevatar.Studio.Hosting/Endpoints/StudioTeamEndpoints.cs
  - src/Aevatar.Studio.Hosting/Endpoints/StudioMemberEndpoints.cs
  - test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/
  - test/Aevatar.Studio.Tests/
non_goals:
  - no edits
  - no new endpoint design beyond the minimal owner resolver recommendation
risk: medium
dependencies: []
validation:
  - read existing endpoint/test paths
done_criteria:
  - reports exact owner resolver landing point
  - reports whether accepted Team stream already exposes enough owner identity
  - reports required backend tests and any architecture guard impact
user_confirmation_required: false
```

### 3.2 Backend Owner Resolver

```yaml
id: backend-owner-resolver
title: Add durable Team run owner resolver and accepted owner envelope
agent_role: logic-worker
semantic_decision: Run owner tuple is captured at acceptance time and reused for historical inspection.
action_safety: Resolver must not replay events or query current entry member as authority.
scope_paths:
  - src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs
  - src/Aevatar.Studio.Application/Studio/Services/StudioTeamGAgentStreamInvocationService.cs
  - src/Aevatar.Studio.Application/Studio/Abstractions/IStudioTeamGAgentStreamInvocationService.cs
  - test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/ScopeServiceStreamInvocationEndpointTests.cs
  - test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/ScopeServiceRunQueryEndpointTests.cs
  - test/Aevatar.Studio.Tests/StudioTeamGAgentStreamInvocationServiceTests.cs
non_goals:
  - no full Team run list or aggregation
  - no waiting controls
  - no process-local run registry
  - no query-time replay
risk: high
dependencies:
  - architecture-contract-map
validation:
  - dotnet test targeted endpoint/service tests
  - architecture guards if query/projection paths are touched
done_criteria:
  - Team stream accepted response/frame includes owner tuple fields
  - `scopeId + teamId + runId` can resolve `ownerMemberId + publishedServiceId + actorId + commandId + correlationId`
  - tests prove entry member changes do not change historical owner
  - tests prove member rebinding does not change historical `publishedServiceId`
user_confirmation_required: true
```

### 3.3 Frontend Route And API Foundation

```yaml
id: frontend-cockpit-foundation
title: Add cockpit route, navigation helper, and owner resolver client
agent_role: logic-worker
semantic_decision: `teamId + runId` route is product context only; owner tuple must be resolved before member-scoped queries.
action_safety: The page must show owner-resolution pending/error states instead of guessing.
scope_paths:
  - apps/aevatar-console-web/config/routes.ts
  - apps/aevatar-console-web/src/shared/navigation/teamRoutes.ts
  - apps/aevatar-console-web/src/shared/navigation/teamRoutes.test.ts
  - apps/aevatar-console-web/src/shared/api/runtimeRunsApi.ts
  - apps/aevatar-console-web/src/shared/api/scopeRuntimeApi.ts
  - apps/aevatar-console-web/src/shared/api/runtimeRunsApi.test.ts
  - apps/aevatar-console-web/src/shared/api/scopeRuntimeApi.test.ts
non_goals:
  - no cockpit visual implementation
  - no localStorage authority
  - no `workflowId` as owner hint
risk: medium
dependencies:
  - backend-owner-resolver contract shape
validation:
  - pnpm --dir apps/aevatar-console-web tsc
  - targeted frontend route/API tests
done_criteria:
  - route `/scopes/:scopeId/teams/:teamId/runs/:runId` exists
  - route parser/builders use distinct `routeTeamId`, `routeRunId`, `ownerMemberId`, `publishedServiceId`
  - owner resolver client is used before member run summary/audit calls
user_confirmation_required: true
```

### 3.4 Durable-First Cockpit Model

```yaml
id: cockpit-view-model
title: Build durable-first cockpit state model
agent_role: logic-worker
semantic_decision: Durable summary/audit owns truth; live frames are current-session evidence only.
action_safety: Accepted pending, audit unavailable, live disconnected, and waiting-without-capability are separate states.
scope_paths:
  - apps/aevatar-console-web/src/pages/teams/
  - apps/aevatar-console-web/src/shared/studio/execution.ts
  - apps/aevatar-console-web/src/shared/api/runtimeRunsApi.ts
  - apps/aevatar-console-web/src/shared/api/scopeRuntimeApi.ts
non_goals:
  - no backend schema change
  - no waiting action controls
  - no raw debug export
risk: medium
dependencies:
  - frontend-cockpit-foundation
validation:
  - focused model/unit tests
done_criteria:
  - durable audit replaces matching live rows
  - final output/failure/status come from durable data
  - live stream disconnected does not change run status
  - unknown sensitivity copy-all omits raw payload bodies
user_confirmation_required: true
```

### 3.5 Team Strip And Cockpit UI

```yaml
id: cockpit-ui
title: Implement Team detail strip and read-only cockpit UI
agent_role: style-worker
semantic_decision: Team context is primary; owner member/runtime identities are visible but secondary; Evidence is drill-down.
action_safety: No waiting action buttons in V1 Core unless typed capabilities exist.
scope_paths:
  - apps/aevatar-console-web/src/pages/teams/detail.tsx
  - apps/aevatar-console-web/src/pages/teams/components/
  - apps/aevatar-console-web/src/pages/teams/tabs/
  - apps/aevatar-console-web/src/pages/teams/detail.test.tsx
  - apps/aevatar-console-web/src/locales/en-US.ts
non_goals:
  - no generic observability dashboard
  - no graph without authoritative layout
  - no decorative/marketing layout
risk: medium
dependencies:
  - cockpit-view-model
validation:
  - pnpm --dir apps/aevatar-console-web tsc
  - pnpm --dir apps/aevatar-console-web test --runInBand targeted tests
  - browser visual check if app can run
done_criteria:
  - Team detail distinguishes `Team current work` from `Entry member latest run`
  - cockpit default view is Timeline
  - Information, Output, Evidence views exist with empty/unavailable states
  - text fits and basic keyboard/accessibility behavior is preserved
user_confirmation_required: true
```

### 3.6 Final Verification

```yaml
id: final-verification
title: Verify PRD authority rules and implementation safety
agent_role: verifier
semantic_decision: Code, tests, and docs must preserve the agreed identity and truth-source model.
action_safety: Unsafe or inferred actions must remain absent from V1 Core.
scope_paths:
  - touched diff
non_goals:
  - no new features
risk: medium
dependencies:
  - backend-owner-resolver
  - frontend-cockpit-foundation
  - cockpit-view-model
  - cockpit-ui
validation:
  - bash tools/ci/test_stability_guards.sh
  - bash tools/ci/workflow_binding_boundary_guard.sh if workflow binding/control paths changed
  - bash tools/ci/query_projection_priming_guard.sh if query/projection paths changed
  - bash tools/ci/projection_state_version_guard.sh if current-state readmodel paths changed
  - pnpm --dir apps/aevatar-console-web tsc
  - pnpm --dir apps/aevatar-console-web test --runInBand
  - targeted dotnet tests for changed backend paths
done_criteria:
  - relevant checks pass or blockers are explicitly reported
  - no production UI uses localStorage as Team run authority
  - no new query-time replay or process-local run fact registry
  - no route/test fixture assumes `memberId === workflowId`
user_confirmation_required: false
```

## 4. Implementation Sequence

### Phase A: Discovery

Run `architecture-contract-map` first. It is read-only and should answer:

- Where can the accepted Team stream expose the owner tuple?
- Does a durable Team invocation discriminator already exist?
- Can the resolver be implemented from existing read models/query contracts?
- What tests already cover Team stream, member run query, and run audit?
- Which guards become mandatory if query/projection code is touched?

### Phase B: Backend Contract

Implement the minimum durable owner path:

- accepted owner envelope;
- cold-start owner resolver;
- tests for entry member changes;
- tests for member rebinding after run acceptance.

Do not build full Team run aggregation in this phase.

### Phase C: Frontend Foundation

Add route and API plumbing only after the backend contract shape is clear:

- canonical cockpit route;
- route parser/builders;
- owner resolver client;
- identity-distinct tests.

### Phase D: Cockpit Data And UI

Build the durable-first model, then UI:

- accepted / durable pending state;
- summary available / audit unavailable state;
- timeline from audit;
- output from durable data;
- evidence with redaction-safe copy;
- live frames as session-only enhancement.

### Phase E: Verification

Run targeted checks first, then broader checks as feasible. If a guard fails, fix the semantic issue rather than suppressing it.

## 5. Risks And Decisions

### Risk: Owner Resolver Source

The biggest risk is whether the current backend has a durable place to answer:

```text
scopeId + teamId + runId -> ownerMemberId + publishedServiceId
```

If not, implementation must add a typed, durable owner record at run acceptance. It must not use an in-memory map.

### Risk: Team Invocation Discriminator

If no durable `invocationSource = "team"` or equivalent exists, Team Detail must use the honest fallback label **Entry member latest run** until the discriminator exists.

### Risk: Audit Completeness

If durable audit cannot rebuild Information lineage, V1 must show unavailable states rather than parse raw JSON heuristically.

### Risk: Redaction

If sensitivity is unknown, copy-all must omit raw payload bodies. Support-only raw export is not V1.

### Risk: Existing Worktree Changes

The original workspace has unrelated frontend edits. Implementation should continue in:

```text
/Users/abigaildeng/Documents/Playground/aevatar-workflow-realtime-visibility-prd
```

Do not revert or overwrite changes in the original workspace.

## 6. Confirmation Gate

Implementation should start only after this plan is confirmed.

Suggested confirmation phrase:

```text
按这个 plan 开始执行
```
