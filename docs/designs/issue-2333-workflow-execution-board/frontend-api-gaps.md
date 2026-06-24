# Workflow Execution Board Frontend API Gaps

## Purpose

The Workflow Execution Board frontend must preserve the PR design target while
remaining honest about current data contracts. Existing APIs can support Team
and Member selection and limited runtime evidence, but they do not provide an
authoritative current workflow node execution read model.

This document lists fields that are supported today, fields that can be shown as
best-effort evidence, and backend contracts required for a complete board.

## Required Endpoints And Usage

The frontend can render the board layout with existing roster APIs, but a
complete Workflow Execution Board needs one new board read endpoint and one
optional saved-configuration endpoint.

### Existing Endpoints Used By The Selection Page

The selection page should continue using the existing Studio Team and Member
roster APIs.

Usage location:

- `/scopes/:scopeId/workflow-board`

Frontend calls:

```ts
studioApi.listTeams(scopeId)
studioApi.listTeamMembers(scopeId, teamId)
studioApi.getTeam(scopeId, teamId)
studioApi.getMember(scopeId, memberId)
```

Purpose:

- Render the Team column.
- Render the Member / Workflow selection column.
- Validate selected Team and Member ownership before navigating to the display
  page.

Still missing from these APIs for the source design:

- authoritative Team `totalMemberCount`, if the UI should display total members
  instead of loaded/selectable members
- Member workflow binding summary
- `workflowId`
- `publishedServiceId`
- display-safe Member role/responsibility text
- display-safe workflow/node hint text

If these fields are not added to the existing roster responses, the selection
page should either show `pending backend contract` in the Workflow slots or read
them from the board endpoint below.

### Existing Endpoints Used As Runtime Evidence

The display page may show latest/recent run information only as isolated runtime
evidence.

Usage location:

- runtime evidence sub-area inside `/scopes/:scopeId/workflow-board/display`

Frontend calls:

```ts
scopeRuntimeApi.listMemberRuns(scopeId, memberId, { take: 1 })
scopeRuntimeApi.getMemberRunAudit(scopeId, memberId, runId, { actorId })
```

Purpose:

- Show latest/recent run evidence when the board read model is not available.
- Help operators see that runtime activity exists.

Restrictions:

- Runtime evidence must not populate current node, node progress, completed
  nodes, pending nodes, failed nodes, board totals, last node update, node
  duration, or row main status.
- `getMemberRunAudit` may only be called when `runId` and `actorId` come from
  the same authoritative run record.
- Raw output and raw audit payload must not be rendered.

### Required New Endpoint: Board Snapshot

This is the main backend gap. The frontend needs one read-model endpoint that
returns the board state only for the user-selected Teams and the selected
Members under those Teams.

Proposed route:

```http
POST /api/scopes/{scopeId}/workflow-board/snapshot
```

Alternative route if this belongs under Team resources:

```http
POST /api/scopes/{scopeId}/teams/workflow-board/snapshot
```

Use the first route unless the backend ownership model requires the second. In
either case, `scopeId` is the authoritative scope and the request must include
Team and Member selection explicitly. The backend must not treat selected
`teamId`s as "all members in these Teams"; the requested `memberIds` define the
display set.

Usage location:

- initial display load for `/scopes/:scopeId/workflow-board/display`
- auto-refresh on the display page
- manual refresh on the display page
- revalidation after restoring selection from URL or local storage

Request:

```ts
type WorkflowExecutionBoardSnapshotRequest = {
  teamSelections: Array<{
    teamId: string;
    // Only these selected members should appear under this team in the response.
    memberIds: string[];
  }>;
  previousWatermark?: string;
};
```

Response:

```ts
type WorkflowExecutionBoardSnapshotResponse = {
  scopeId: string;
  generatedAt: string;
  watermark: string;
  lastNodeUpdatedAt?: string;
  teams: WorkflowExecutionBoardTeam[];
  totals: WorkflowExecutionBoardTotals;
  invalidSelections?: WorkflowExecutionBoardInvalidSelection[];
};

type WorkflowExecutionBoardInvalidSelection = {
  teamId?: string;
  memberId?: string;
  reason:
    | "team_not_found"
    | "member_not_found"
    | "member_not_in_team"
    | "unauthorized"
    | "archived"
    | "unknown";
  message?: string;
};

type WorkflowExecutionBoardTeam = {
  teamId: string;
  teamName: string;
  totalMemberCount?: number;
  selectedMemberCount: number;
  // Only selected, valid members for this team. Not the full team roster.
  members: WorkflowExecutionBoardMember[];
};

type WorkflowExecutionBoardMember = {
  memberId: string;
  displayName?: string;
  workflowId?: string;
  workflowName?: string;
  publishedServiceId?: string;
  actorId?: string;
  roleSummary?: string;
  currentExecutionId?: string;
  currentNode?: WorkflowExecutionBoardNode;
  completedNodes: WorkflowExecutionBoardCompletedNode[];
  pendingNodes: WorkflowExecutionBoardPendingNode[];
  failedNodes: WorkflowExecutionBoardFailedNode[];
  displaySummary?: string;
  safePreview?: string;
  safeErrorSummary?: string;
  lastNodeUpdatedAt?: string;
};

type WorkflowExecutionBoardNode = {
  nodeId: string;
  name: string;
  status: "running" | "waiting" | "pending" | "failed" | "completed" | "unknown";
  startedAt?: string;
  updatedAt?: string;
  durationMs?: number;
  progress?: number;
};

type WorkflowExecutionBoardCompletedNode = {
  nodeId: string;
  name: string;
  completedAt: string;
  durationMs?: number;
};

type WorkflowExecutionBoardPendingNode = {
  nodeId: string;
  name: string;
  status?: "waiting" | "pending" | "queued" | "unknown";
  reason?: string;
};

type WorkflowExecutionBoardFailedNode = {
  nodeId: string;
  name: string;
  safeErrorSummary: string;
  failedAt?: string;
};

type WorkflowExecutionBoardTotals = {
  completedSteps: number;
  runningNodes: number;
  waitingOrPendingNodes: number;
  failedNodes: number;
};
```

Frontend use:

- `generatedAt`: show board data generation time and reject stale responses.
- `watermark`: use as `previousWatermark` on the next refresh.
- `lastNodeUpdatedAt`: fill the design's last node update field.
- `teams`: render only requested Team groups and only requested Member rows
  under each Team.
- `totals`: fill Completed steps, Running nodes, Waiting / pending, and Failed
  nodes overview cards for the selected Member set only.
- `invalidSelections`: show invalid/unavailable Team or Member rows and block
  runtime evidence requests for those rows.

Required semantics:

- The response is a read model built from committed workflow execution facts or
  an equivalent durable projection feed.
- The endpoint must not require the frontend to replay events or reconstruct
  actor state.
- The endpoint must scope all board data to the submitted `teamSelections`.
- The endpoint must validate `scopeId -> teamId -> memberId` ownership.
- The endpoint must return only valid requested members. It must not expand a
  selected Team to all Team members.
- Missing or unauthorized selections should be reported in `invalidSelections`
  rather than silently omitted.
- The backend must provide all IDs explicitly; the frontend must not infer them.

### Optional New Endpoint: Saved Board Configuration

The first implementation can keep selection in URL/local storage. A backend
configuration endpoint is optional, but it becomes useful if board selections
need to be shared, restored across devices, or audited.

Proposed routes:

```http
POST /api/scopes/{scopeId}/workflow-board/configs
GET /api/scopes/{scopeId}/workflow-board/configs/{boardConfigId}
PATCH /api/scopes/{scopeId}/workflow-board/configs/{boardConfigId}
DELETE /api/scopes/{scopeId}/workflow-board/configs/{boardConfigId}
```

Usage location:

- selection page save/share action
- display page restore-by-configuration flow

Required semantics:

- `boardConfigId` is a saved display configuration identity only.
- It is not a Team, Member, Workflow, Service, or Actor identity.
- Saved configuration must store `scopeId`, selected `teamId`s, selected
  `memberId`s, display name, owner, and timestamps.
- It must not store workflow execution state.

## Supported Today

### Scope Route

The board should be scoped by route:

- `/scopes/:scopeId/workflow-board`
- `/scopes/:scopeId/workflow-board/display`

`scopeId` from the path is the authoritative scope.

### Team Roster

Existing frontend API:

```ts
studioApi.listTeams(scopeId)
studioApi.getTeam(scopeId, teamId)
```

Can support:

- Team display name
- `teamId`
- Team selection

### Team Member Roster

Existing frontend API:

```ts
studioApi.listTeamMembers(scopeId, teamId)
studioApi.getMember(scopeId, memberId)
```

Can support:

- `memberId`
- validated `scopeId -> teamId -> memberId` relationship
- Member selection
- selected Team count
- selected Member count

If member count is computed from loaded roster rows, label it as loaded or
selectable member count. Do not present it as an authoritative total unless the
backend returns a `totalCount`.

## Best-Effort Runtime Evidence

Existing frontend API:

```ts
scopeRuntimeApi.listMemberRuns(scopeId, memberId, { take: 1 })
```

May be shown only as isolated runtime evidence.

It must not populate:

- current node
- node progress
- completed nodes
- pending nodes
- failed nodes
- board totals
- last node update
- node duration
- row main status

If shown, label it as runtime evidence or latest/recent run evidence.

Existing frontend API:

```ts
scopeRuntimeApi.getMemberRunAudit(scopeId, memberId, runId, { actorId })
```

May be used only when:

- `runId` comes from an authoritative run record.
- `actorId` comes from the same authoritative run record.
- The UI does not render raw payloads or raw output.

Audit evidence must not infer current node, completed node order, waiting nodes,
failed nodes, progress, or totals.

## Gap Fields

The following design fields are required by the Workflow Execution Board but
cannot be filled authoritatively by the currently listed frontend APIs.

Until a backend read model provides them, the frontend must show `unavailable`
or `pending backend contract`.

### Board Totals

- completed steps
- running nodes
- waiting or pending nodes
- failed nodes

Do not display `0` for these totals unless the backend explicitly returns `0`.
Do not aggregate them from latest run status or audit evidence.

### Node State

- current node ID
- current node display name
- current node status
- node progress
- node start time
- node update time
- node duration

Do not use the last audit event as current node.

### Completed Nodes

- ordered completed node list
- completed node timestamp
- completed node duration

Do not derive completed-node order from audit event order.

### Pending or Waiting Nodes

- ordered pending node list
- waiting node status
- waiting reason
- pending node display name

Do not infer waiting state from missing events.

### Failed Nodes

- failed node ID
- failed node display name
- failed node status
- safe failed-node summary

Do not display raw stack traces, raw provider responses, raw tool output, user
prompts, or raw actor payloads.

### Last Node Update

The design includes last node update. Existing frontend refresh time is not the
same thing.

Display:

- current time: supported by client
- last refresh: supported by frontend request completion
- last node update: gap until backend provides it

## Required Backend Contract

A complete board needs an actor/read-model backed board snapshot or equivalent
current execution read model.

Suggested shape:

```ts
type WorkflowExecutionBoardSnapshot = {
  scopeId: string;
  generatedAt: string;
  watermark: string;
  lastNodeUpdatedAt?: string;
  teams: WorkflowExecutionBoardTeam[];
  totals: WorkflowExecutionBoardTotals;
};

type WorkflowExecutionBoardTeam = {
  teamId: string;
  teamName: string;
  members: WorkflowExecutionBoardMember[];
};

type WorkflowExecutionBoardMember = {
  memberId: string;
  workflowId?: string;
  publishedServiceId?: string;
  actorId?: string;
  displayName?: string;
  currentExecutionId?: string;
  currentNode?: WorkflowExecutionBoardNode;
  completedNodes: WorkflowExecutionBoardCompletedNode[];
  pendingNodes: WorkflowExecutionBoardPendingNode[];
  failedNodes: WorkflowExecutionBoardFailedNode[];
  displaySummary?: string;
  safePreview?: string;
};

type WorkflowExecutionBoardNode = {
  nodeId: string;
  name: string;
  status: "running" | "waiting" | "pending" | "failed" | "completed" | "unknown";
  startedAt?: string;
  updatedAt?: string;
  durationMs?: number;
  progress?: number;
};

type WorkflowExecutionBoardCompletedNode = {
  nodeId: string;
  name: string;
  completedAt: string;
  durationMs?: number;
};

type WorkflowExecutionBoardPendingNode = {
  nodeId: string;
  name: string;
  status?: "waiting" | "pending" | "queued" | "unknown";
};

type WorkflowExecutionBoardFailedNode = {
  nodeId: string;
  name: string;
  safeErrorSummary: string;
  failedAt?: string;
};

type WorkflowExecutionBoardTotals = {
  completedSteps: number;
  runningNodes: number;
  waitingOrPendingNodes: number;
  failedNodes: number;
};
```

Required semantics:

- `scopeId`, `teamId`, `memberId`, `workflowId`, `publishedServiceId`, and
  `actorId` must be explicit fields. The frontend must not infer them.
- `workflowId` is workflow/draft/definition identity, not `memberId`.
- `publishedServiceId` is callable runtime identity, not `memberId`.
- `actorId` belongs to runtime execution/evidence. It is not a stable member
  identity.
- Node order must be provided by the backend.
- Board totals must be provided by the backend.
- `watermark` must allow refreshes to reject stale data and support future
  incremental updates.
- All display summaries must be safe for large-screen display. The frontend
  will still render them as plain text and length-limit them.

## Why Latest Runs And Audit Are Not Enough

Latest run data is useful runtime evidence, but it is not an authoritative
workflow node state model.

It cannot answer:

- which workflow node is currently executing
- which nodes are waiting
- which nodes are pending
- which nodes failed
- whether there are zero running nodes
- the authoritative completed-node order
- board-level totals
- last node update

Audit data is historical evidence. Without a backend contract, the frontend
must not replay audit events to reconstruct current state on the query path.

## Frontend Behavior Until Backend Contract Exists

The frontend should:

- Render the PR design layout.
- Keep the Workflow Execution Board product name.
- Display supported Team and Member data.
- Display runtime evidence in a visually isolated area.
- Display `unavailable` or `pending backend contract` for gap fields.
- Link this document from implementation notes or PR description.

The frontend should not:

- Rename the product to Latest Runs Board.
- Fill gap metrics with `0`.
- Derive node state from audit events.
- Derive totals from latest run status.
- Show raw runtime output.
- Treat actor identity as member identity.
