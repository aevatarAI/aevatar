---
title: Workflow Activity authoritative contracts
status: approved
owner: Aevatar frontend
---

# Workflow Activity Authoritative Contracts Design

## Context

Issue #3245 coordinates backend contracts needed by the Workflow Activity vNext frontend. The completed backend work on `feature/integrate` covers three capabilities:

- #3250: a typed, cursor-paginated Activity feed;
- #3251: authoritative recovery capabilities and a fork receipt with a routable `newRunId`;
- #3252: durable retry/fork and sub-workflow lineage.

This design applies those contracts to the frontend on top of `feat/2026-08-04_workflow-activity-vnext`. It supersedes the older client-side assumptions in the original vNext design where the new backend facts now exist.

## Goals

- Make Activity useful for identifying and investigating exact runs without exposing technical actor identities.
- Scale Activity history through the backend cursor contract.
- Make recovery eligibility, alternatives, reuse, revision, and cost warnings backend-owned.
- Route fork receipts and lineage with public `runId` values only.
- Keep retry/fork lineage distinct from sub-workflow lineage.

## Non-Goals

- Do not implement #3253 version-aligned Run Detail surfaces.
- Do not implement #3254 usage provenance, currency, or the broader five-surface Run Detail redesign.
- Do not infer workflow, run, actor, member, service, or definition identities.
- Do not merge `feature/integrate` into the frontend branch; it is the contract source, not this PR's base.
- Do not add production mocks, optimistic lineage, or fabricated recovery facts.

## Contract Boundary

The shared workflow Activity model will add explicit types for:

- `WorkflowActivityRunFeedFilter` and `WorkflowActivityRunFeedPage`;
- authoritative Activity row facts, including initiator, redacted input summary, current step, first failure, waiting state, timestamps, duration, recovery capability, and lineage;
- recovery eligibility, unavailable reason codes, recommended actions, and action semantics;
- retry/fork lineage and sub-workflow lineage as separate structures;
- fork receipts containing both public `newRunId` and technical `newRunActorId`.

The API adapter will add `listActivityRuns(scopeId, filter)` for `GET /api/workflow/observatory/activity-runs`. Existing `listRuns`, detail, and graph methods remain because the backend introduced an additive feed rather than replacing every observatory endpoint.

Decoders remain strict. Malformed required identities, cursor envelopes, recovery fields, or lineage fields fail at the transport boundary instead of leaking partially trusted objects into pages.

## Activity Page

The Activity page will use TanStack Query cursor pagination. Its server query key includes scope, status, origin, definition, workflow, and time-range filters. Changing any server filter creates a new query and discards the prior cursor chain.

The page sends `workflowId` directly. It removes the extra Workflow-detail lookup and no longer converts `workflowId` into `definitionActorId`.

The default table groups backend facts into a compact operational view:

- Workflow: workflow name and a secondary short run reference;
- Status: status plus the first available failure, waiting, or current-step context;
- Started: localized start time and backend duration or a live elapsed value;
- Initiator: user-readable initiator plus run source;
- Input: the backend-redacted input summary;
- Action: open the exact run.

Actor ids, command ids, projection fields, and raw payloads are excluded from the default table. Mobile retains workflow, status, time, and the open action through the existing responsive table treatment.

The initial request asks for `includeTotalCount=true`. The page displays loaded and total counts when the backend supplies the total. `Load more` uses the opaque `nextCursor`; the client does not inspect or synthesize it. A next-page failure preserves loaded rows and provides a local retry. A `malformed_cursor` response offers a refresh from the first page.

The existing `q` filter remains client-only and is labeled as filtering loaded runs so it does not imply a full-history server search.

## Recovery

Run Detail reads top-level `recoveryCapability`. It removes `resolveRunRecovery(steps, graph)` and never derives eligibility from failed-step count or graph root shape.

Retry and Run again are enabled only when their respective capability has `eligibility=eligible` and a non-blank `startingStepId`. An unavailable action remains keyboard-focusable with `aria-disabled` and a visible backend reason. Recommended alternatives are presented in user language; only routes backed by existing stable identities are navigable.

Before dispatch, the confirmation modal shows:

- the source input;
- definition revision id and version;
- starting step;
- whether prior step outputs are reused;
- that a separate run will be created and the source run remains immutable;
- a model/tool cost warning only when the capability says cost may recur.

The fork request continues using the source run id, authoritative starting step, and source input required by the current endpoint. HTTP 202 is rendered as accepted, not completed. Its primary action opens the new Run Detail route using `newRunId`. `newRunActorId`, command id, correlation id, and status URL remain in Technical details.

## Lineage

Run Detail adds a Related runs section with two independent groups:

- Retry history: source, original, attempt, starting step, and child runs;
- Sub-workflows: parent, root, depth, parent step, and child runs.

Every non-blank public run id links through `buildWorkflowActivityRunHref(scopeId, runId)`. Actor ids are never routing inputs and appear only in Technical details. Unavailable or legacy lineage renders an honest unavailable message without inventing relationships.

## Error Handling

- Initial Activity failure uses the existing full-page error state.
- Next-page failure is local to the pagination footer and preserves prior pages.
- Strict decoder failures surface through the existing Activity API error path.
- Fork failure leaves the confirmation available for retry and shows the existing toast treatment.
- Missing or unavailable capability disables dispatch with the backend reason.
- Lineage unavailability does not fail the whole Run Detail page.

## Testing

Focused Jest tests will cover:

- feed query encoding, pagination envelope decoding, authoritative workflow id filtering, recovery/lineage decoding, and `newRunId` receipt decoding;
- Activity initial loading/error/empty states, rich authoritative row facts, URL-backed filters, cursor append/reset behavior, next-page failure preservation, and exact run navigation;
- Run Detail capability-driven action states, focusable unavailable reasons, confirmation disclosures, immutable fork request, accepted receipt navigation via `newRunId`, and separate lineage groups.

Local verification follows the personal incremental frontend policy: run the scope analyzer, every changed test file, dependency-related focused tests, and changed-file static checks only. Full frontend suite, full typecheck when no affected target exists, and production build are delegated to GitHub CI.

