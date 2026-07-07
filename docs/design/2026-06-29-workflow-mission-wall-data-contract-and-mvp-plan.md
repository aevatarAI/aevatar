---
title: "Workflow Mission Wall Data Contract And MVP Plan"
status: draft
owner: tbd
last_updated: 2026-06-30
references:
  - "./2026-06-29-workflow-mission-wall-product-package.md"
  - "./2026-06-29-workflow-mission-wall-wireframe.md"
  - "../adr/0015-agui-sse-projection-session-pipeline.md"
  - "../adr/0023-two-tier-inspector-architecture.md"
---

# Workflow Mission Wall Data Contract And MVP Plan

## 1. Product Boundary

The Mission Wall is a presentation surface over existing Aevatar workflow execution facts.

It must not:

1. Query actor state directly.
2. Reconstruct durable facts from logs, OTel buffers, or process-local maps.
3. Create a second projection pipeline.
4. Treat live AGUI/SSE/OTel events as durable truth.

It may:

1. Query readmodels.
2. Use live events for current-screen animation.
3. Reconcile live deltas with subsequent readmodel refresh.
4. Link to Run Inspector and external trace systems.

## 2. Canonical Data Sources

| Source | Truth Level | Wall Use | Inspector Use |
|---|---|---|---|
| `ScopeMemberRunSummary` / `ScopeServiceRunSummary` | Durable published run summary | Live Run Window cards, running/failed/completed counters, state version | overview |
| `ScopeServiceRunAuditReport` / `WorkflowRunReport` | Durable run audit/report | selected run graph, step status, timeline, role replies, state version | report, steps, role replies |
| `WorkflowExecutionCurrentState` | Durable current state | fallback/source readmodel behind run summary | overview |
| `WorkflowRunTimeline` | Durable event timeline | Inspector timeline and event detail | timeline/logs |
| `WorkflowRunGraphArtifact` | Durable topology artifact | topology nodes/edges | topology |
| AGUI/SSE run events | Live observation | node pulse, edge flow, current deltas | current stream panel |
| OTel trace/span | Live/deep trace observation | external link badge only | trace links and span detail |
| Team/member readmodels | Durable team context | team/member labels, grouping | context |

Identity rule:

1. The wall displays `teamName`, `entryMemberName`, `memberName`, `step`, `tool`, and `gate` as primary labels.
2. Runtime ids such as actor ids are implementation identifiers. They may be carried as `runtimeActorId` for joins, trace links, and Run Inspector copy/debug affordances.
3. A missing Team/Member label should render as an honest unknown member label, not fall back to exposing an actor id on the big screen.

## 3. Proposed Presentation Contracts

These contracts are frontend-facing DTO shapes. They can initially be assembled from existing query responses in the frontend adapter. Stable missing semantics should later become typed backend fields.

### 3.1 `MissionWallSnapshot`

```typescript
type MissionWallSnapshot = {
  generatedAt: string;
  live: MissionWallLiveState;
  summary: MissionWallSummary;
  runs: MissionWallRun[];
  focus: MissionWallFocus;
  topology: MissionWallTopology;
};
```

Field semantics:

| Field | Meaning |
|---|---|
| `generatedAt` | Time this wall snapshot was assembled |
| `live` | Live stream health and observation freshness |
| `summary` | Aggregate counters |
| `runs` | Wall-visible running/recent/priority-pinned run cards |
| `focus` | Current run expanded in the center graph and the reason it was chosen |
| `topology` | Selected/global graph |

### 3.2 `MissionWallLiveState`

```typescript
type MissionWallLiveState = {
  status: "live" | "degraded" | "disconnected" | "idle";
  message: string;
  lastObservedAt?: string;
  durableFreshnessSeconds?: number;
};
```

Rules:

1. `status` is about live observation, not durable run status.
2. `durableFreshnessSeconds` comes from readmodel timestamps.
3. If SSE disconnects, status becomes `degraded` or `disconnected` while readmodel data remains visible.
4. Do not show a single global `v42` unless the backend exposes a real aggregate projection watermark. Run versions belong to selected/current runs.

### 3.3 `MissionWallSummary`

```typescript
type MissionWallSummary = {
  runningRuns: number;
  wallVisibleRuns: number;
  waitingHuman: number;
  failedRuns: number;
  retryingRuns: number;
  recentlyCompletedRuns?: number;
  completedToday?: number;
  avgLatencyMs?: number;
  projectionFreshnessSeconds?: number;
};
```

Source mapping:

| Field | Source |
|---|---|
| `runningRuns` | current-state query where `completionStatus = running` |
| `wallVisibleRuns` | running + recently completed + priority-pinned failed/waiting runs |
| `waitingHuman` | run current state, timeline/report summary, or future waiting reason readmodel |
| `failedRuns` | current-state query where `completionStatus = failed/timed_out` |
| `retryingRuns` | report/timeline summary |
| `recentlyCompletedRuns` | current-state/report query filtered by completed status and retention window |
| `completedToday` | future aggregate query |
| `avgLatencyMs` | report summary / future aggregate |
| `projectionFreshnessSeconds` | readmodel updatedAt |

### 3.4 `MissionWallRun`

```typescript
type MissionWallRun = {
  id: string;
  runId: string;
  commandId?: string;
  teamId?: string;
  teamName?: string;
  entryMemberId?: string;
  entryMemberName?: string;
  workflowName: string;
  status: MissionWallRunStatus;
  currentStepId?: string;
  currentStepLabel?: string;
  currentMemberId?: string;
  currentMemberName?: string;
  currentInformationCategory?: string;
  startedAt?: string;
  updatedAt?: string;
  durationMs?: number;
  progress?: MissionWallProgress;
  stateVersion?: number;
  lastEventId?: string;
  runtimeActorId?: string;
  visibilityReason: "running" | "recently_completed" | "priority_pinned";
  visibleUntil?: string;
  priorityLevel: "none" | "info" | "warning" | "error";
  focusPriority: number;
  focusReason?: MissionWallFocusReason;
  focusExplain?: string;
};
```

```typescript
type MissionWallRunStatus =
  | "running"
  | "completed"
  | "waiting"
  | "failed"
  | "timed_out"
  | "retrying"
  | "stopped"
  | "stale"
  | "unknown";

type MissionWallProgress = {
  completedSteps: number;
  totalSteps: number;
};

type MissionWallFocusReason =
  | "failed"
  | "timed_out"
  | "waiting_human"
  | "stale_projection"
  | "stale_live"
  | "retrying"
  | "latest_running"
  | "recently_completed";
```

Existing sources:

| Field | Existing Source | Gap |
|---|---|---|
| `runId` | `WorkflowExecutionCurrentState.RunId` | none |
| `commandId` | current state/report | none |
| `runtimeActorId` | current state/report root actor id | keep inspector/debug only |
| `teamId/teamName` | Studio team readmodel | needs join/query composition |
| `entryMemberId/entryMemberName` | Studio team/member readmodels or invocation context | may need typed wall composition |
| `currentMemberId/currentMemberName` | report role replies + Studio member label mapping | needs stable member mapping |
| `workflowName` | current state/report | none |
| `status` | current state/report | waiting/retrying may need better typed status |
| `currentStepId` | report step traces / timeline | needs stable current step derivation |
| `currentInformationCategory` | no stable field | add typed summary later |
| `progress` | report summary | none |
| `stateVersion` | `ScopeMemberRunSummary.stateVersion` or `ScopeServiceRunAuditReport.stateVersion` | none |
| `lastEventId` | `ScopeMemberRunSummary.lastEventId` or `ScopeServiceRunAuditReport.lastEventId` | none |
| `visibilityReason` | wall presentation rule over readmodel status/timestamps | no backend field needed for MVP |
| `focusPriority/focusReason/focusExplain` | wall director adapter rules | no backend field needed for MVP |

Run visibility and failure rules:

```typescript
const isRunning = run.completionStatus === "running";
const isCompleted = run.completionStatus === "completed";
const isFailed = run.completionStatus === "failed" || run.completionStatus === "timed_out";

const recentlyCompleted =
  isCompleted && now - Date.parse(run.lastUpdatedAt ?? "") < COMPLETED_RETENTION_MS;

const priorityPinned =
  isFailed ||
  hasWaitingHumanPriority(run) ||
  hasStaleProjectionPriority(run);

const wallVisible =
  isRunning || recentlyCompleted || priorityPinned;
```

Rules:

1. Published means the workflow/member service can be invoked; it does not make a run active.
2. `runningRuns` counts published runs whose current readmodel status is `running`.
3. Fast completed runs stay in the wall-visible window for a short retention period, for example 3-5 minutes.
4. Failed, timed-out, waiting-human, and stale-projection runs stay pinned longer, for example 15-30 minutes or until acknowledged in the inspector.
5. A timeline's latest failed event is not enough to mark the run failed; use the run/report `completionStatus` readmodel. Timeline events can explain the failure, not define the final run state.

### 3.5 `MissionWallFocus`

```typescript
type MissionWallFocus = {
  runId?: string;
  reason?: MissionWallFocusReason;
  explain?: string;
  selectedAt?: string;
  minDwellUntil?: string;
};
```

Focus director rules:

```typescript
function chooseFocusRun(
  runs: MissionWallRun[],
  currentFocus: MissionWallFocus,
  now: number,
): MissionWallRun | undefined {
  const visible = runs.filter(isWallVisible);
  const candidate =
    newest(visible.filter((run) => run.focusReason === "failed" || run.focusReason === "timed_out")) ??
    oldestWaiting(visible.filter((run) => run.focusReason === "waiting_human")) ??
    stalest(visible.filter((run) => run.focusReason === "stale_projection" || run.focusReason === "stale_live")) ??
    newest(visible.filter((run) => run.focusReason === "retrying")) ??
    newestUpdated(visible.filter((run) => run.focusReason === "latest_running")) ??
    newestCompleted(visible.filter((run) => run.focusReason === "recently_completed"));

  if (!candidate) return undefined;
  if (!currentFocus.runId) return candidate;
  const current = runs.find((run) => run.runId === currentFocus.runId);
  const currentPriority = current?.focusPriority ?? 0;
  if (candidate.focusPriority >= currentPriority + 200) return candidate;
  if (now < Date.parse(currentFocus.minDwellUntil ?? "")) return current;
  return candidate;
}
```

MVP focus priority:

| Focus reason | Priority |
|---|---:|
| `failed` / `timed_out` | 1000 |
| `waiting_human` | 900 |
| `stale_projection` / `stale_live` | 800 |
| `retrying` | 700 |
| `latest_running` | 500 |
| `recently_completed` | 300 |

Rules:

1. Focus is deterministic and testable.
2. Focus selection never queries actor state or event store directly.
3. Minimum dwell time prevents the center graph from jumping too often.
4. A higher-severity event can interrupt the dwell time.
5. The UI should show a short focus explanation, for example `Focused because: waiting for approval 2m`.

### 3.6 `MissionWallTopology`

```typescript
type MissionWallTopology = {
  scope: "global" | "team" | "run";
  mode: "workflow_step_graph" | "runtime_topology";
  selectedRunId?: string;
  workflowGraph?: MissionWallWorkflowGraph;
  runtimeTopology?: MissionWallRuntimeTopology;
};
```

Rules:

1. MVP defaults to `workflow_step_graph`.
2. `runtime_topology` is optional and should reuse `MissionControl/TopologyCanvas` only when the user intentionally switches to runtime topology.
3. The step graph should reuse `GraphCanvas` studio variant data shape where possible.

### 3.7 `MissionWallWorkflowGraph`

```typescript
type MissionWallWorkflowGraph = {
  nodes: MissionWallWorkflowStepNode[];
  edges: MissionWallWorkflowStepEdge[];
  layout?: MissionWallWorkflowLayout;
  selectedStepId?: string;
};

type MissionWallWorkflowLayout = {
  engine: "manual" | "elk_layered";
  direction: "right" | "down";
  totalSteps?: number;
  windowStartIndex?: number;
  windowEndIndex?: number;
  viewportStepIds?: string[];
  stepOverview?: MissionWallWorkflowOverviewStep[];
};

type MissionWallWorkflowOverviewStep = {
  stepId: string;
  index: number;
  status: MissionWallStepStatus;
};

type MissionWallWorkflowStepNode = {
  id: string;
  stepId: string;
  stepType: string;
  targetRole?: string;
  parametersSummary?: string;
  status: MissionWallStepStatus;
  focused?: boolean;
  runId?: string;
  runtimeActorId?: string;
  outputPreview?: string;
  error?: string;
  latencyMs?: number;
  position?: { x: number; y: number };
};

type MissionWallWorkflowStepEdge = {
  id: string;
  fromStepId: string;
  toStepId: string;
  kind: "next" | "branch";
  branchLabel?: string;
  traversed?: boolean;
  focused?: boolean;
};

type MissionWallStepStatus =
  | "idle"
  | "active"
  | "completed"
  | "waiting"
  | "failed"
  | "retrying"
  | "unknown";
```

Mapping:

| Step Graph Field | Existing Input | Gap |
|---|---|---|
| `stepId` / `stepType` / `targetRole` | `StudioGraphNodeData`, report step traces, audit steps | none |
| `parametersSummary` | existing `GraphCanvas` / Studio graph summary | keep wall-safe |
| `status` | existing `executionStatus` / audit step success/request/completion/suspension | retrying may need typed status |
| `focused` | selected step or latest run trace item | none |
| `outputPreview` / `error` | report/audit step trace | sanitize for wall |
| `layout` | frontend adapter via ELKjs layered layout or fallback layout | add `elkjs` dependency if long workflows need automatic layout |
| `edges` | existing workflow next/branch edges | none |

Layout rules:

1. `elk_layered` is a presentation layout strategy, not a durable workflow fact.
2. ELKjs should receive stable node dimensions, step edges, branch labels, and a left-to-right direction for the wall.
3. The adapter should compute all node `position` values before passing nodes to `GraphCanvas`.
4. For long workflows, the wall renders `viewportStepIds` in the enlarged center graph and uses `stepOverview` for the Workflow Step Overview.
5. If ELK layout fails or is unavailable, fallback to the existing GraphCanvas/MemberPublishedRunsReplay layout.

### 3.8 `MissionWallRuntimeTopology`

```typescript
type MissionWallRuntimeTopology = {
  nodes: MissionWallRuntimeNode[];
  edges: MissionWallRuntimeEdge[];
};

type MissionWallRuntimeNode = {
  id: string;
  label: string;
  kind: string;
  status: "idle" | "active" | "waiting" | "completed" | "failed" | "unknown";
  runtimeActorId?: string;
  summary?: string;
};

type MissionWallRuntimeEdge = {
  id: string;
  source: string;
  target: string;
  label?: string;
  streaming?: boolean;
};
```

Rules:

1. Runtime topology is secondary in MVP.
2. Runtime nodes must still be labeled with Team/Member/role-friendly names when shown on the wall.
3. Live-only runtime edges can animate but should reconcile or disappear after refresh.

### 3.9 Run Inspector Detail Items

The MVP wall does not render a separate priority list or an event detail area. The following shapes are reserved for Run Inspector handoff only; they are not part of the large-screen wall layout.

#### `RunInspectorPriorityItem`

```typescript
type RunInspectorPriorityItem = {
  id: string;
  severity: "info" | "warning" | "error";
  type:
    | "failed"
    | "waiting_human_input"
    | "waiting_approval"
    | "timeout"
    | "retrying"
    | "stale_live_stream"
    | "stale_projection";
  runId: string;
  teamId?: string;
  memberId?: string;
  runtimeActorId?: string;
  stepId?: string;
  title: string;
  summary: string;
  ageSeconds?: number;
  createdAt?: string;
  actionLabel?: string;
};
```

Priority derivation:

| Type | Durable Source | Live Source |
|---|---|---|
| failed | current state/report final error | run error event |
| waiting_human_input | timeline/current status | human input request event |
| waiting_approval | timeline/current status | approval request event |
| timeout | timeline/report step error | timeout event |
| retrying | timeline/report retry events | retry live event |
| stale_live_stream | connection state | SSE heartbeat/timeout |
| stale_projection | readmodel updatedAt/stateVersion | none |

#### `RunInspectorEventItem`

```typescript
type RunInspectorEventItem = {
  id: string;
  timestamp: string;
  severity: "info" | "success" | "warning" | "error";
  runId?: string;
  teamId?: string;
  memberId?: string;
  runtimeActorId?: string;
  stepId?: string;
  title: string;
  summary: string;
  durationMs?: number;
  source: "readmodel" | "live";
};
```

Good examples:

```text
risk_review active · llm_call · role: reviewer
Risk Reviewer found 2 issues · waiting approval
ChronoStorage query completed · 42 records
Projection updated · WorkflowRunInsightReport · selected run v42
```

Bad examples:

```text
workflow_loop: step=analyze completed success=true output=(234 chars)
{ "eventEnvelope": { ... } }
```

## 4. MVP Data Adapter

Initial implementation can use a frontend adapter:

```text
ScopeMemberRunSummary / ScopeServiceRunSummary
ScopeServiceRunAuditReport / WorkflowRunReport
MemberPublishedRunsReplay audit-step-to-GraphCanvas mapping
RunsTracePane timeline/messages/events summaries
Studio team/member label lookups
AGUI/SSE current run events for live animation
  -> buildMissionWallSnapshot()
```

Adapter responsibilities:

1. Normalize readmodel query responses.
2. Map published run audit steps to `GraphCanvas` workflow-step topology.
3. Derive focus priority/reason with deterministic wall-director rules.
4. Derive run-card badges for waiting/failed/retrying/stale states.
5. Mark live-only state as non-durable.

Adapter must not:

1. Store durable facts in a service-level in-memory map.
2. Query event store.
3. Replay events in query path.
4. Parse actor id prefixes as business semantics.
5. Render runtime actor ids as primary wall labels.

## 5. Backend Gap List

MVP can start with existing APIs. These gaps should be tracked for production quality:

| Gap | Proposed Fix |
|---|---|
| Live run window query by team/scope | Add query/readmodel endpoint for running, recent, and priority-pinned runs; do not scan actor state |
| Current step is derived indirectly | Add typed current step summary to readmodel |
| Tool call summaries are live-heavy | Add durable typed tool call summary if stable consumption exists |
| Inspector/future priority details derived in UI | Add readmodel-owned priority projection only if it becomes a shared feature |
| Team/member labels require joins | Add composition query or wall readmodel |
| Trace id may not be attached | Propagate trace id into run/report when available |
| Information category missing | Add typed safe summary/category field |

## 6. MVP Implementation Plan

### Phase 1: Static Wall From Readmodels

Scope:

1. Add route for wall prototype in Console or keep as design HTML first.
2. Fetch wall-visible or selected run readmodel data.
3. Render top strip, live run window, topology, and focus reason.
4. No live animation required.

Acceptance:

1. Refresh restores wall state from readmodels.
2. No raw JSON/log appears in primary wall.
3. Selecting a run updates topology.
4. Run cards and focus reason show failed/waiting/retrying/stale states.

Verification:

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
```

### Phase 2: Live Animation Layer

Scope:

1. Subscribe to AGUI/SSE for current run.
2. Animate active nodes and edges.
3. Show live/degraded/disconnected state.
4. Reconcile live deltas with readmodel polling.

Acceptance:

1. Disconnecting SSE marks live layer degraded.
2. Durable state remains visible.
3. Live-only edge is marked non-durable.
4. Readmodel refresh overwrites stale live presentation.

### Phase 3: Run Inspector Handoff

Scope:

1. Click run card, graph node, focus reason, or freshness badge.
2. Open Run Inspector with selected context.
3. Reuse existing Runs timeline/messages/events where practical.

Acceptance:

1. Context is preserved.
2. Timeline focuses selected event/step.
3. Raw payload stays in advanced tab.
4. Copyable ids are available in inspector.

### Phase 4: Typed Backend Enhancements

Scope:

1. Add typed readmodel fields for stable wall semantics.
2. Add priority/waiting readmodel if multiple consumers need it.
3. Add trace id links if OTel drilldown is supported.
4. Add tests and architecture guards for new query boundaries.

Acceptance:

1. Query path reads readmodels only.
2. No query-time replay.
3. No process-local fact registry.
4. Protobuf contracts model stable semantics.

## 7. Engineering Story Breakdown

| Story | Risk | Dependencies |
|---|---|---|
| Build wall presentation adapter | Low | existing runtime APIs |
| Build static wall route | Medium | design system / route decision |
| Adapt topology canvas | Medium | `@xyflow/react`, graph data |
| Add priority derivation | Medium | status/timeline mapping |
| Add live animation | Medium | AGUI/SSE connection |
| Add inspector handoff | Low/Medium | existing Runs pages |
| Add typed backend fields | High | proto/readmodel/projector updates |
| Add priority/waiting readmodel | High | durable semantic decision |

## 8. Verification Matrix

| Change Type | Required Verification |
|---|---|
| Docs only | `bash tools/docs/lint.sh` |
| Frontend wall route | `pnpm --dir apps/aevatar-console-web tsc`; relevant tests |
| Tests changed | `bash tools/ci/test_stability_guards.sh` |
| Readmodel/current-state changes | `bash tools/ci/projection_state_version_guard.sh`; `bash tools/ci/projection_state_mirror_current_state_guard.sh` |
| Query/projection lifecycle changes | `bash tools/ci/query_projection_priming_guard.sh` |
| Workflow binding/resume/signal | `bash tools/ci/workflow_binding_boundary_guard.sh` |
| Architecture-sensitive change | `bash tools/ci/architecture_guards.sh` |

## 9. Open Decisions

1. Route name: `/runtime/mission-wall`, `/workflow-wall`, or under Team detail?
2. Scope default: global, current tenant/scope, selected team, or selected run?
3. Does wall need authentication mode separate from Console?
4. What retention windows should completed and priority-pinned runs use?
5. Should live stream subscribe to one selected run or all wall-visible running runs?
6. Should priority acknowledgement exist on wall or only inspector?
7. Should first production version include token/cost metrics?
8. Which external trace provider is first-class: Jaeger, Phoenix, Langfuse, or none?

## 10. First Prototype Data

Static prototype should use sample objects:

1. `Risk Review`: running, 8/12 steps, Research Member active, Approval Gate waiting.
2. `Customer Onboarding`: retrying, CRM Lookup failed once.
3. `Billing Reconciliation`: stale projection warning.

Sample inspector event detail:

```text
19:12 collect_sources completed · retrieve_facts · 3 sources · 1.8s
19:13 ChronoStorage query completed · 42 records · 820ms
19:14 Risk Reviewer found 2 issues · waiting approval
19:15 CRM Lookup failed · retry 2/3
19:16 WorkflowRunInsightReport projected · selected run v42 · 2s fresh
```

Sample priority items:

```text
Approval required · Risk Review · release_gate · 2m
Tool call failed · Customer Onboarding · crm_lookup · retrying
Projection delayed · Billing Reconciliation · 34s stale
```
