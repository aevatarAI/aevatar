# Workflow Execution Board Consensus Plan

## Context

This document records the final product and implementation boundary for the
Aevatar Workflow Execution Board after a fresh three-round ChatGPT redteam
review.

Source design intent comes from PR #2336. The design target is a large-screen
Workflow Execution Board, not a latest-runs list. Current API limitations must
be documented as backend gaps; they must not silently rename or downgrade the
product target.

Fresh review conversation:
https://chatgpt.com/c/6a3b5596-0b74-83ee-a5b4-cb39dff071e3

## Product Target

Build the frontend surface for an Aevatar Workflow Execution Board.

The board has two screens:

- Source selection page: a dark, three-column setup page where a signed-in user
  selects Teams and Members under the current scope.
- Display page: a dark, large-screen Workflow node execution board grouped by
  selected Team, then selected Member, matching the source design layout.

The display page keeps the design goal and visual slots for:

- current node
- node progress
- completed node sequence
- pending or waiting nodes
- failed node and safe error summary
- board totals for completed steps, running nodes, waiting or pending nodes,
  and failed nodes
- current time, last refresh time, and last node update

These slots must be capability-aware. If the current backend cannot provide a
field as an authoritative read model, the UI must show `unavailable` or
`pending backend contract`. It must not display `0`, infer values, or hide the
gap.

## Final Redteam Outcome

Round 1 established that the product target must stay as Workflow Execution
Board while every field is classified as `supported`, `best-effort`, or `gap`.
It rejected deriving current node state, node progress, totals, or last node
update from latest runs or audit evidence.

Round 2 accepted the direction and required five refinements:

- The display page must revalidate selection from the backend; it must not trust
  URL, local storage, or selection-page state.
- `actorId` must not be part of `BoardMemberIdentity`; it belongs to a specific
  run evidence record.
- Latest run evidence must be visually isolated and must not drive row main
  status, color, sorting, totals, or node duration.
- Team member count must be labelled as loaded or selectable count unless the
  backend returns an authoritative `totalCount`.
- Even safe summaries must be rendered as bounded plain text and must not
  participate in state inference.

Round 3 accepted the final v3 boundary and agreed there are no remaining
blockers to implementation under that boundary.

Final review boundary:

> Allow implementing the PR design's Workflow Execution Board frontend layout
> and selection, refresh, and validation chain. The first version may only show
> data that existing APIs authoritatively support plus isolated runtime
> evidence. Workflow node execution state, progress, overview totals, and last
> node update must remain `gap` or `unavailable` until the backend provides a
> current execution read model. They must not be derived from latest runs or
> audit evidence.

## Routes

Use scope-owned routes:

- `/scopes/:scopeId/workflow-board`
- `/scopes/:scopeId/workflow-board/display`

Rules:

- Reuse the current Console authentication protection.
- Do not make either route public.
- Do not add hidden compatibility entries under `/teams/:scopeId`.
- Treat path `scopeId` as the single authoritative scope.
- If a selection payload contains `scopeId`, it must match the path `scopeId`;
  otherwise reject it and ask the user to reselect.

## Selection Page

The source selection page should preserve the PR design:

- top label: `WORKFLOW DISPLAY SOURCE`
- title: select the Team / Member whose workflow should be displayed
- account status: signed-in state
- left column: Team multi-select
- middle column: Member / Workflow multi-select
- right column: display summary and enter-board action

Supported fields:

- Team name and `teamId` from the Team read model.
- Member identity and Team relationship from
  `studioApi.listTeamMembers(scopeId, teamId)`.
- Selected Team and selected Member counts from frontend selection state.

Best-effort fields:

- Team member count, if computed from returned Team members. Label it as loaded
  or selectable members unless the backend provides a `totalCount`.

Gap fields:

- `workflowId`, workflow name, workflow binding status, and node hints, unless
  the backend returns them explicitly. These must render as
  `pending backend contract`.

Selection shape:

```ts
type BoardSelection = {
  scopeId: string;
  teams: Array<{
    teamId: string;
    memberIds: string[];
  }>;
};
```

Selection limits:

- At least one Team and one Member are required.
- The first version should allow at most 4 Teams and 24 Members.

## Identity Model

Keep stable identities separate.

```ts
type BoardMemberIdentity = {
  scopeId: string;
  teamId: string;
  memberId: string;
  workflowId?: string;
  publishedServiceId?: string;
};
```

`actorId` is not a member identity. It may only appear as part of run evidence:

```ts
type RunEvidenceIdentity = {
  scopeId: string;
  teamId: string;
  memberId: string;
  runId: string;
  actorId: string;
};
```

Rules:

- Row keys must be `scopeId:teamId:memberId`.
- Never derive `workflowId`, `publishedServiceId`, or `actorId` from
  `memberId`, route position, string prefix, or cached old values.
- Never derive Team membership from a Member alone.
- Display-page validation must reload Team and Member read models and verify
  `scopeId -> teamId -> memberId` before requesting runtime evidence.
- Invalid Team or Member selections must be marked `invalid` or `unavailable`
  and must not trigger runtime evidence requests.
- The display page must render only the selected Members under each selected
  Team. It must not expand a selected Team into all Team members.

## Display Page

The display page should keep the PR layout:

- title: `Workflow 节点执行看板`
- current time
- last refresh time
- last node update
- overview cards for completed steps, running nodes, waiting or pending nodes,
  and failed nodes
- selected Team sections
- selected Member rows with current node, progress, completed nodes, pending
  nodes, failed nodes, and isolated runtime evidence

Supported fields:

- Current time, from the client clock.
- Last refresh time, from frontend request completion.
- Team name and `teamId`.
- Member identity and validated Team membership.

Gap fields until a backend board/current-execution read model exists:

- last node update
- completed steps total
- running nodes total
- waiting or pending nodes total
- failed nodes total
- current node
- node progress
- completed node sequence
- pending or waiting nodes
- failed node and safe failed-node summary
- node duration

Gap fields must render as `unavailable` or `pending backend contract`. They
must not render as `0` and must not participate in derived totals.

Runtime evidence:

- Latest or recent run evidence may be shown in an isolated runtime evidence
  area.
- Latest run status, duration, and color must not drive row main status,
  current-node status, sorting, Team totals, overview cards, or node duration.
- Audit evidence may be loaded only when `runId` and `actorId` come from the
  same authoritative run record.
- Audit evidence must not infer current node, completed node sequence, pending
  nodes, failed nodes, progress, or totals.

## Existing API Usage

Supported data sources:

```ts
studioApi.listTeams(scopeId)
studioApi.listTeamMembers(scopeId, teamId)
studioApi.getTeam(scopeId, teamId)
studioApi.getMember(scopeId, memberId)
```

Best-effort runtime evidence:

```ts
scopeRuntimeApi.listMemberRuns(scopeId, memberId, { take: 1 })
```

Conditional runtime evidence:

```ts
scopeRuntimeApi.getMemberRunAudit(scopeId, memberId, runId, { actorId })
```

Conditions:

- `runId` and `actorId` must come from the same authoritative run record.
- If `actorId` is missing, do not call audit.
- Do not display raw `lastOutput`, raw audit payload, stack traces, tool output,
  user prompts, provider responses, or actor internal state.
- Only backend-declared `safePreview`, `displaySummary`, or
  `safeErrorSummary` may be displayed.
- Safe summaries must still be rendered as plain text, length-limited, and
  excluded from state inference.

## Backend Interface Summary

The first frontend implementation can render the board shell and supported Team
and Member fields with existing APIs. A complete Workflow Execution Board needs
a new board read endpoint:

```http
POST /api/scopes/{scopeId}/workflow-board/snapshot
```

Frontend usage:

- display page initial load
- display page auto-refresh
- display page manual refresh
- validation after restoring selection from URL or local storage

Request shape:

```ts
type WorkflowExecutionBoardSnapshotRequest = {
  teamSelections: Array<{
    teamId: string;
    // Only these selected members should be returned under this team.
    memberIds: string[];
  }>;
  previousWatermark?: string;
};
```

The endpoint must return validated Team and Member rows, explicit workflow and
runtime identities, current node state, ordered completed/pending/failed nodes,
board totals, last node update, `generatedAt`, and a refresh `watermark`.
Returned Team rows must include only the selected Members for that Team, and
totals must be computed only over the selected Member set.

Detailed field requirements and the optional saved-board configuration endpoint
are documented in
`docs/designs/issue-2333-workflow-execution-board/frontend-api-gaps.md`.

## Refresh Rules

- Default interval must be at least 30 seconds.
- Refresh only supported and best-effort evidence fields.
- Do not refresh gap fields as if they are data.
- Do not allow overlapping refreshes.
- Abort or ignore stale requests when selection changes.
- Stale responses must not overwrite the newer selection or newer refresh.
- Pause or reduce refresh when the page is hidden.
- Apply backoff after consecutive failures.
- Limit member run requests to a small concurrency window, such as 4 to 6.
- Manual refresh must follow the same concurrency and non-overlap rules.

## Required Tests

Cover at least:

- Routes require authentication.
- No `/teams/:scopeId` workflow-board route is added.
- Path `scopeId` mismatch with selection `scopeId` is rejected.
- Display page revalidates selection from Team and Member read models.
- Invalid Team or Member selections block runtime evidence requests.
- Fixture identities differ, for example:
  - `memberId = "m-alpha"`
  - `workflowId = "wf-alpha"`
  - `publishedServiceId = "svc-alpha"`
  - `actorId = "actor-alpha"`
- `actorId` is not part of `BoardMemberIdentity`.
- Audit uses only the `actorId` from the same run record.
- Current node, progress, and totals are not filled from latest run or audit.
- Gap overview cards display `unavailable`, not `0`.
- Latest run evidence is visually isolated and does not drive row main status.
- Display output contains only selected Members under selected Teams.
- Overview totals, once backend-supported, are scoped to the selected Member
  set only.
- Raw output and raw payload text are not rendered.
- Safe summaries are plain text and length-limited.
- Stale responses do not override a newer selection.
- Selection limits are enforced.

## Implementation Decision

Proceed with implementation under this boundary:

- Keep the product as Workflow Execution Board.
- Implement the source design's layout and flow.
- Use existing APIs only for fields they authoritatively support.
- Show explicit gap states for backend-owned workflow execution fields.
- Document the backend contracts needed to turn gap fields into real board data.
