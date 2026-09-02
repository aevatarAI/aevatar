---
title: "Workflow Mission Wall Backend Handoff"
status: draft
owner: tbd
last_updated: 2026-06-30
references:
  - "./2026-06-29-workflow-mission-wall-product-package.md"
  - "./2026-06-29-workflow-mission-wall-data-contract-and-mvp-plan.md"
  - "./2026-06-29-workflow-mission-wall-wireframe.md"
  - "../adr/0023-two-tier-inspector-architecture.md"
backend_source_branch: "origin/feature/integrate@3dbac3f96ec7"
frontend_source_branch: "docs/2026-06-29_workflow-mission-wall-product-package@0d77146627fd"
---

# Workflow Mission Wall Backend Handoff

## 1. Decision

This handoff records backend work needed for the Workflow Mission Wall. No backend implementation is part of the frontend MVP branch. The current frontend implementation uses the existing authenticated Studio and runtime endpoints directly; the wall-specific backend endpoint below is a future optimization, not a blocker for the current frontend package.

The frontend MVP owns:

1. `MissionWallSnapshot` presentation DTOs.
2. Deterministic wall focus rules.
3. Large-screen rendering for top status, live run window, focus reason, workflow step graph, and step overview.
4. A real-data adapter over existing `getAuthSession`, `listMembers`, `listTeams`, `listServices`, `listServiceRuns`, and `getServiceRunAudit` APIs.
5. A clear adapter boundary that can later consume a wall-specific readmodel-backed API.

The backend owns:

1. Durable run discovery for wall-visible runs.
2. Typed readmodel fields for current step, waiting/retrying/stale reasons, safe summaries, trace links, and team/member labels.
3. Composition queries that join run facts with Team/Member context without exposing actor ids as primary wall labels.
4. Readmodel/query-port implementation and tests that preserve CQRS and projection boundaries.

## 2. Backend Facts Observed On `feature/integrate`

The backend source of truth for this handoff is `origin/feature/integrate` at commit `3dbac3f96ec7`.

Observed existing HTTP/query surfaces:

1. `GET /api/scopes/{scopeId}/runs`
2. `GET /api/scopes/{scopeId}/runs/{runId}`
3. `GET /api/scopes/{scopeId}/runs/{runId}/audit`
4. `GET /api/scopes/{scopeId}/members/{memberId}/runs`
5. `GET /api/scopes/{scopeId}/members/{memberId}/runs/{runId}`
6. `GET /api/scopes/{scopeId}/members/{memberId}/runs/{runId}/audit`
7. `GET /api/scopes/{scopeId}/services/{serviceId}/runs`
8. `GET /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}`
9. `GET /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}/audit`

Observed existing application/query models:

1. `WorkflowActorCurrentStateListQuery` supports scope, definition actor ids, run origin, schedule ids, status, and updated-at filters.
2. `IWorkflowExecutionCurrentStateQueryPort` exposes workflow actor current-state readmodel queries.
3. `WorkflowRunReport` exposes durable run report data: completion status, workflow name, root actor id, command id, state version, timestamps, duration, input/output/error, topology, step traces, role replies, timeline, usage, and summary.
4. `ScopeServiceRunSummary` and service/member audit responses already expose useful run summary and audit fields to the frontend.

Observed gap:

No wall-specific backend composition endpoint or `MissionWallSnapshot` readmodel was found on `feature/integrate`.

Current frontend workaround:

1. Resolve authenticated `scopeId` through `GET /api/auth/me`, unless the route explicitly provides `scopeId`.
2. Load workflow members with `GET /api/scopes/{scopeId}/members` for Studio member/team labels and `publishedServiceId` mapping only.
3. Optionally load team labels with `GET /api/scopes/{scopeId}/teams`.
4. Load the real runtime service catalog with `GET /api/scopes/{scopeId}/services?take=200`.
5. Match workflow members to services with `StudioMemberSummary.publishedServiceId === ServiceCatalogSnapshot.serviceId`.
6. Fan out `GET /api/scopes/{scopeId}/services/{serviceId}/runs?take=50` only for services that exist in the runtime catalog.
7. Load the right-side selected graph through `GET /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}/audit?actorId={actorId}`.

This path preserves the identity boundary: `memberId` is the Studio member authority used for labels and team ownership, `publishedServiceId`/`serviceId` is the runtime service identity used for runs and audit, and `workflowId` remains a draft/definition hint from member implementation metadata. The wall does not pass `workflowId` or `publishedServiceId` into member-run APIs.

Important runtime observation:

`GET /api/scopes/{scopeId}/members/{memberId}/runs` resolves a Studio member to a published service internally. A Studio roster member can exist while its expected runtime service is absent; in that case the backend returns `SCOPE_SERVICE_NOT_FOUND`. For wall-wide discovery, the frontend must not blindly fan out member-run requests over the Studio roster. The service catalog is the runtime authority for whether a published service can currently be queried.

## 3. Required Backend Contract

The preferred future production contract is a readmodel-backed composition query:

```text
GET /api/scopes/{scopeId}/mission-wall
```

Optional filters:

```text
teamId
memberId
take
updatedFrom
updatedTo
includeRecentlyCompleted
includePriorityPinned
```

The response should be equivalent to the frontend presentation contract:

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

Backend may choose a more backend-native response name, but the field semantics should remain aligned with the frontend presentation contract. Stable business semantics must be typed fields or typed sub-messages, not an unstructured bag.

## 4. Data Ownership

Wall data must come from readmodels and durable run artifacts:

1. Run status, state version, last event id, timestamps, progress, and final error come from current-state/report readmodels.
2. Step graph and step status come from `WorkflowRunReport` step traces/topology or a typed graph artifact derived from committed run facts.
3. Team/member labels come from Studio Team/Member readmodels or an explicit composition query.
4. Live AGUI/SSE state may animate the current screen but must not define durable run status.
5. OTel trace/span data may appear as links or inspector detail only; it must not be the wall's truth source.

The backend must not:

1. Query actor state directly from the wall endpoint.
2. Replay events in the request path.
3. Build wall-visible facts from process-local maps.
4. Parse actor id prefixes to infer team/member/workflow semantics.
5. Expose `rootActorId`, `actorId`, or `primaryActorId` as the wall's primary labels.

## 5. Field Gaps To Close

| Gap                               | Existing source                                                                              | Backend follow-up                                                                                                                                                                  |
| --------------------------------- | -------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Scope/team wall-visible run query | Member/service/default run list endpoints exist, but the wall needs a scope/team composition | Add a readmodel-backed wall run query that returns running, recently completed, waiting, retrying, failed, timed-out, stale, and priority-pinned runs without scanning actor state |
| Team/member labels                | Member-scoped responses know `memberId`; service reports expose runtime ids                  | Compose `teamId`, `teamName`, `entryMemberId`, `entryMemberName`, `currentMemberId`, and `currentMemberName` from Team/Member readmodels                                           |
| Current step                      | `WorkflowRunReport.Steps` and timeline allow frontend derivation                             | Add typed current step summary to the run summary or wall DTO                                                                                                                      |
| Waiting/retrying status           | Suspension fields and timeline/report hints exist                                            | Add typed `waitingReason`, `retryingReason`, or stable status fields if wall and inspector both consume them                                                                       |
| Safe information category         | No stable wall-safe field was observed                                                       | Add typed safe summary/category field, explicitly separate from raw prompt/output/log payloads                                                                                     |
| Trace link                        | OTel semantic conventions exist, but run report attachment is not guaranteed                 | Propagate trace id/link into run report or a trace-link readmodel when available                                                                                                   |
| Projection freshness              | Run-level `stateVersion`, `lastEventId`, and timestamps exist                                | Expose aggregate wall freshness only if backed by a real projection watermark; otherwise keep version scoped to selected/current runs                                              |

## 6. Suggested Backend Phases

### Phase A: Composition Query

Build a scope/team wall-visible run query over existing readmodels:

1. Filter by `completionStatus` and `updatedAt` for running/recent/failed/timed-out runs.
2. Include waiting/retrying/stale flags only from typed readmodel/report facts.
3. Join Team/Member labels through explicit Studio readmodels.
4. Return page-sized results with durable timestamps and state versions.

Acceptance:

1. Query reads readmodels only.
2. No query-time event replay.
3. No process-local registry or actor-id-to-context dictionary.
4. Missing Team/Member label is represented as unknown, not actor id fallback.

### Phase B: Step Summary

Add typed step summaries needed by the wall:

1. `currentStepId`
2. `currentStepLabel`
3. `currentMemberId`
4. `currentMemberName`
5. `waitingReason`
6. `retryingReason`
7. `safeOutputPreview`

Acceptance:

1. Values are produced from committed workflow run facts.
2. The query path does not derive them by replaying events.
3. Raw prompt/output stays out of the wall DTO unless explicitly marked as inspector-only.

### Phase C: Production Wall Snapshot

Optionally return a full `MissionWallSnapshot` from backend if multiple clients need the same composition:

1. Durable counters.
2. Wall-visible runs.
3. Selected/focus run topology.
4. Projection freshness.
5. Trace links.

Acceptance:

1. Focus rules are deterministic and test-covered.
2. The backend does not invent stronger freshness guarantees than the underlying readmodels provide.
3. The DTO preserves identity boundaries: `memberId`, `workflowId`, and `publishedServiceId` remain separate.

## 7. Frontend Integration Boundary

The frontend MVP should continue using `MissionWallSnapshot` as a presentation boundary. When backend support lands, replace the sample/source adapter with the readmodel-backed fetcher.

Do not push backend-only fields into the primary wall UI merely because they are easy to fetch. Runtime ids belong in Run Inspector debug sections, copy affordances, or trace links, not the big-screen labels.
