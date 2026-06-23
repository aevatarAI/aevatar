---
title: "Team Workflow Realtime Visibility PRD"
status: agreed-prd
owner: codex
created: 2026-06-22
branch: docs/2026-06-22_workflow-realtime-visibility-prd
review_round: 3
review_consensus: codex + chatgpt
---

# Team Workflow Realtime Visibility PRD

## 0. Decision Summary

Build a **Team Run Cockpit**: a Team-owned product surface for inspecting **member-owned workflow runs** in realtime and after refresh.

The Team owns the operator context. The entry member and its `publishedServiceId` own execution. `runId` identifies one execution. Live frames make the run feel realtime, but durable run summary and audit remain the source of truth.

V1 is deliberately narrower than a full observability console:

- It supports Teams whose entry member is a published workflow member.
- It makes Team-started workflow runs visible from Team detail.
- It opens a read-only cockpit for a known run owner tuple.
- It renders durable summary, timeline, output, evidence, and limited information lineage from existing or explicitly required durable contracts.
- It treats live AGUI/SSE frames as session enhancement only.
- It requires a minimal durable owner resolver so copied/reopened run links work after browser refresh.
- It does not infer Team state from local browser storage, current entry member guesses, raw status strings, or process-local registries.

## 1. Identity Model And Authority

### 1.1 Stable Identities

The product and implementation must preserve these identity boundaries:

| Identity | Meaning | Product Use |
| --- | --- | --- |
| `scopeId` | Workspace boundary | Top-level resource owner |
| `teamId` | Studio Team ownership surface | Product context and Team route |
| `entryMemberId` | Current member selected as Team entry point | Used to start new Team runs |
| `ownerMemberId` | Member that owned a specific accepted run | Used to inspect historical run |
| `workflowId` | Draft/definition document identity hint | Never used as member/service/run identity |
| `publishedServiceId` | Callable runtime service identity owned by a member | Runtime invocation target |
| `runId` | One execution instance | Run detail identity |
| `actorId` | Runtime actor address for the run | Diagnostics and run control target where required |
| `commandId` / `correlationId` | Request tracing identities | Diagnostics and dispatch correlation |

Hard rules:

- `teamId` is not the runtime owner.
- `entryMemberId` is valid for starting new Team runs, but historical run inspection must use the immutable owner identity captured when the run was accepted.
- `ownerMemberId` is the product name for the member that actually owns a run.
- `publishedServiceId` is the product/runtime service identity; do not downgrade it to a vague `serviceId` in product contracts.
- Historical `publishedServiceId` must be the value captured at run acceptance time. It must not be recomputed from the member's current binding after the member has been rebound.
- `workflowId` must never fetch, infer, or substitute for `teamId`, `memberId`, `publishedServiceId`, or `runId`.
- A query hint such as `memberId` is only navigation convenience. It must be verified against durable run owner metadata before use.

### 1.2 Immutable Run Owner Tuple

Run inspection is authoritative only when the page has, or can resolve, this tuple:

```text
scopeId + teamId context + ownerMemberId + publishedServiceId + runId
```

`teamId context` explains why the user is looking at the run. It does not prove who executed the run.

The tuple must be captured at run acceptance time and carried into durable run summary/audit, or returned by a Team-scoped run index. The page must not reopen a historical run by resolving the Team's **current** entry member and hoping it matches the run's owner.

V1 requires a minimal durable owner resolver:

```text
scopeId + teamId + runId -> ownerMemberId + publishedServiceId + actorId + commandId + correlationId
```

This resolver is smaller than a full Team run index. It exists only to reopen a known Team-context run link without guessing the owner. A future Team run index may list and aggregate many runs, but V1 cold-start reopen must not depend on that future index.

### 1.3 Team Invocation Source

Team Detail may say "this Team is processing X" only when the run has durable Team invocation context:

```json
{
  "scopeId": "scope-alpha",
  "teamId": "team-alpha",
  "ownerMemberId": "m-alpha",
  "publishedServiceId": "svc-alpha",
  "runId": "run-alpha",
  "invocationSource": "team",
  "commandId": "cmd-alpha",
  "correlationId": "corr-alpha"
}
```

If V1 only has member-scoped run history without a Team invocation discriminator, the UI must label the strip honestly as **Entry member latest run**, not **Team current work**.

## 2. Product Problem

The product can already run workflow members and collect pieces of evidence:

- Team/member invoke can stream AGUI frames while a run is active.
- Member-scoped run endpoints expose run lists, summaries, and audit artifacts.
- Workflow Studio can parse execution frames into run, step, waiting, usage, output, and raw-event views.
- Runtime/Mission surfaces expose some run health.

The experience is still fragmented. A Team operator cannot look at the Team and answer:

- What work did this Team just accept?
- Which member and published service actually own this run?
- What information entered the workflow?
- Which step is active, completed, waiting, or failed?
- What did each step receive and produce?
- What final output is backed by durable evidence?
- What can the user safely copy or share for support?

The desired mental model is not "invoke something and inspect logs." It is "watch the Team work, then reopen the run later with the same truth."

## 3. Users

### Primary: Team Operator

- Starts and monitors Team work from Team detail.
- Needs current status, waiting/failure explanation, and final output without reading raw frames first.
- Thinks in Teams and members, not actor addresses.

### Secondary: Workflow Builder

- Uses the cockpit while testing a published workflow member through the Team surface.
- Needs step input/output, role/tool interaction, usage, and errors.
- Needs confidence that the displayed workflow matches the published run, not a stale draft.

### Tertiary: Support / Engineering

- Needs raw event frames, run ids, actor ids, command ids, state versions, audit links, and copyable diagnostics.
- Must respect redaction and permission boundaries.

## 4. V1 Scope

### 4.1 In Scope

V1 covers:

- Team detail live work strip for a workflow entry member.
- Starting a Team run and receiving an accepted run owner envelope.
- Read-only Team Run Cockpit for a verified owner tuple.
- Durable run summary and durable run audit rendering.
- Ordered timeline as the default view.
- Final output view.
- Evidence view with redaction-safe copy.
- Live AGUI/SSE frames as current-session enhancement.
- Honest states for accepted-but-not-materialized, audit unavailable, and live disconnected.
- Identity-boundary tests with distinct id shapes.

### 4.2 Conditional In Scope

These are included only when the backend exposes typed contracts:

- Step-level Information lineage after refresh.
- Workflow map layout.

If typed contracts are absent, V1 must degrade to durable summary, ordered timeline, output, and evidence rather than invent behavior from raw frames.

Waiting controls are not part of V1 Core. They belong to V1.5 unless the backend already exposes typed waiting capabilities before implementation starts.

### 4.3 Out Of Scope

- Global distributed observability.
- Full multi-member Team run list or aggregation beyond the V1 owner resolver.
- Non-workflow Team member cockpit.
- Run comparison.
- Latency heatmaps.
- Support-only raw debug mode.
- Audit retention policy UI.
- Published revision versus draft diff.
- Browser localStorage as authoritative run state.
- Query-time replay, projection priming, or process-local `runId -> context` registries.

## 5. Source Of Truth

| Question | Durable Source | Live Source | UI May Infer? |
| --- | --- | --- | --- |
| Which Team is this? | Team read model | None | No |
| Which member starts new Team runs? | Team `entryMemberId` + member read model | None | No |
| Who owns this historical run? | Accepted run owner envelope or Team-scoped run index | None | No |
| Which published service executed it? | Durable owner tuple / member binding at acceptance | None | No |
| Current run status | Run summary / workflow current-state read model | Live lifecycle frames as temporary display | No final status inference |
| Completed/total steps | Run summary or audit | Live step frames as temporary display | No durable overwrite |
| Waiting kind | Typed run/audit capability contract | Live waiting frames as temporary display | No string/status inference |
| Available actions | Typed backend capabilities | None | No |
| Final output | Durable run summary/audit | Live output frame as temporary preview | Durable wins |
| Failure reason | Durable run summary/audit | Live error frame as temporary preview | Durable wins |
| Timeline after refresh | Durable audit artifact | None | No |
| Realtime animation | None | Current SSE/AGUI stream | Yes, as live-only decoration |
| Redaction | Typed redaction/secure fields | Typed live redaction flags only | No |

Durable state has precedence over live state when both describe the same event, step, output, or error. Live frames must never overwrite a durable result with a higher `stateVersion`, newer `lastEventId`, or verified audit event.

## 6. Run Owner Resolution

### 6.1 Starting A Team Run

When a user starts a Team run:

1. The frontend calls the Team stream endpoint.
2. The backend resolves the current Team entry member.
3. The member-owned `publishedServiceId` receives the invocation.
4. The backend returns or streams an accepted owner envelope before the cockpit pins the run:

```json
{
  "scopeId": "scope-alpha",
  "teamId": "team-alpha",
  "ownerMemberId": "m-alpha",
  "publishedServiceId": "svc-alpha",
  "runId": "run-alpha",
  "actorId": "actor-alpha",
  "commandId": "cmd-alpha",
  "correlationId": "corr-alpha",
  "ackStage": "dispatch_accepted"
}
```

The accepted envelope means the command entered runtime dispatch. It does not mean the member exists in a fresh read model, the workflow completed, or audit is visible.

### 6.2 Reopening A Run

When opening `/scopes/:scopeId/teams/:teamId/runs/:runId`, the page must resolve the owner tuple by one of these paths:

1. The V1 owner resolver returns `ownerMemberId`, `publishedServiceId`, and runtime ids for `teamId + runId`.
2. A future Team-scoped run index returns the same owner tuple.
3. The route carries `ownerMemberId` as a hint and the page verifies it against durable run owner metadata.
4. The user arrives from a just-accepted run and the page holds the accepted owner envelope while waiting for durable materialization.

The page must not use the current Team `entryMemberId` as the historical run owner unless durable run owner metadata verifies it.

The page must also not recompute `publishedServiceId` from the owner member's current binding. Rebinding a member after a run starts must not change which historical published service the cockpit uses for that run.

### 6.3 Team Latest Work

Team Detail can show **Team current work** only for runs with durable `invocationSource = "team"` and matching `teamId`.

If only member run history is available:

- Show **Entry member latest run**.
- Display the entry member identity.
- Avoid saying the Team itself is processing that run.
- Link to the cockpit only when the owner tuple can be verified.

## 7. Product Surfaces

### 7.1 Team Detail Live Work Strip

Location:

- `/scopes/:scopeId/teams/:teamId`

Purpose:

- Provide a compact, honest signal for recent Team-entry work.

Fields:

- Team name and lifecycle.
- Entry member display name.
- Published workflow capability state.
- Latest verified Team run, or entry member latest run if Team attribution is unavailable.
- Display status.
- Workflow name.
- Progress count.
- Last updated time.
- Last output/error preview.

Sources:

- Team read model.
- Member read model.
- Team-scoped run index, or verified member run summary.

Empty states:

- No entry member.
- Entry member not workflow-capable.
- Entry member not bound.
- No verified Team runs.
- Accepted run awaiting durable summary.

Forbidden behavior:

- Do not read local recent runs as authority.
- Do not label member-only history as Team work.
- Do not infer `publishedServiceId` from route strings.

### 7.2 Team Run Cockpit

Canonical route:

- `/scopes/:scopeId/teams/:teamId/runs/:runId`

Required route semantics:

- Path identifies Team context and run id.
- Owner identity must be resolved or verified before member-scoped run queries.
- `workflowId` is not accepted as a run owner hint.

Layout:

- Header: Team, owner member, workflow name, display status, elapsed time, durable freshness.
- Main: ordered timeline.
- Side inspector: selected step or run detail.
- Secondary tabs: `Information`, `Output`, `Evidence`.

Default view:

- `Timeline`.

Forbidden behavior:

- Do not show a graph generated from the current draft workflow for a historical published run.
- Do not claim live-only frames are durable.
- Do not expose unredacted evidence through copy/export.

### 7.3 Timeline

Purpose:

- Show how the workflow advanced in human terms.

V1 source:

- Durable audit artifact when available.
- Live frames as current-session temporary rows.

Rows:

- Run accepted / started.
- Step requested.
- Step completed / failed.
- Human input or approval requested.
- Waiting for signal.
- Tool call started / ended.
- Message chunks grouped into message rows.
- Usage/cost/latency.
- Run completed / failed / stopped.

Workflow map rule:

- V1 uses the audit artifact's step order as the authority.
- A graphical map may render only when the run audit or verified published revision provides layout for that run.
- If layout is unavailable, the cockpit uses an ordered timeline.
- The current draft definition must not explain a historical published run unless the run explicitly references that exact revision.

### 7.4 Information

Purpose:

- Explain what information the run processed.

V1 behavior:

- Show sections only when typed durable audit or typed live frames provide safe data.
- Otherwise show "Not emitted" or "Audit does not include this detail" instead of inventing lineage.

Sections:

- Run input.
- Step inputs.
- Step outputs.
- External interactions.
- File refs.
- Assigned variables and branch choices.
- Redaction status.

Forbidden behavior:

- Do not parse arbitrary raw JSON text to infer secure values.
- Do not show secure human input except through typed redacted fields.

### 7.5 Output

Purpose:

- Make the final result inspectable without digging through logs.

Source priority:

1. Durable audit final output.
2. Durable run summary output.
3. Live output preview, labeled as live-only.

Content:

- Final output.
- Status and completion reason.
- Last successful step.
- Last error if failed.
- Usage summary when available.
- Copy output.

Rules:

- Prefer business output text over raw JSON.
- If no output has been emitted, say so.
- If durable output differs from live output, durable output replaces live preview and the Evidence tab records the discrepancy.

### 7.6 Evidence

Purpose:

- Preserve debuggability without making raw diagnostics the primary product.

Content:

- Durable run summary.
- Durable audit artifact.
- Live browser-session frames, clearly labeled as incomplete after refresh.
- Owner tuple and runtime identities.
- Redaction-safe copy actions.

Rules:

- Evidence is debuggability, not a redaction bypass.
- Default copy bundle includes only redacted or non-sensitive fields.
- When sensitivity is unknown, copy-all excludes raw payload bodies and includes only safe identifiers, timestamps, event names, redacted previews, and run owner ids.
- Copy selected raw payload is available only if the payload is marked safe by contract or the user has an authorized debug mode in a future version.
- Label this area `Evidence`, not `Metadata`.

## 8. Realtime Merge Model

Live frames improve immediacy. They do not own truth.

Merge rules:

- Live frames are kept in current browser memory only.
- Live rows are keyed by the most precise available identity: `eventId`, then `runId + stepId + eventType + timestamp`, then local sequence as a last resort.
- Durable audit rows replace matching live rows.
- Durable run summary owns display status, final output, failure reason, completed steps, and state version.
- If durable summary is older than live frames, the UI may show a freshness note: `Live activity observed; durable summary has not caught up`.
- If the stream disconnects, the run status does not become failed. The live source status becomes `disconnected`, and durable polling continues.
- When durable audit appears, the cockpit rebuilds the timeline from audit and keeps live-only unmatched rows in a clearly labeled section until they are matched or discarded.

Display statuses:

```text
accepted
running
waiting
completed
failed
stopped
unknown
```

Source statuses:

```text
live_connected
live_disconnected
durable_pending
durable_available
audit_unavailable
```

Source status is not run status.

## 9. Waiting Action Model

V1 Core is read-only. Waiting rows can explain what the run is waiting for, but they do not render action buttons unless the backend already exposes typed capabilities.

V1.5 renders waiting actions only from typed backend capabilities.

Example shape:

```json
{
  "waitingKind": "human_approval",
  "availableActions": [
    {
      "type": "approve",
      "command": "resume",
      "requiresPayload": false
    },
    {
      "type": "reject",
      "command": "resume",
      "requiresPayload": true
    }
  ]
}
```

Rules:

- Do not infer actions from status strings, custom payload names, step labels, or raw payload text.
- `waiting` without capabilities is informational only.
- Action receipts are shown as accepted, not completed.
- The page waits for durable run updates before claiming that an action succeeded.
- If capabilities are not available, the cockpit remains read-only for waiting rows.

## 10. Redaction And Safety

Data safety is a product requirement, not a UI polish task.

Rules:

- Redaction must come from typed contract fields.
- Secure human input must render only redacted output.
- Evidence copy/export must use redacted payloads by default.
- Raw event display must show sensitivity state when known.
- Unknown sensitivity should be treated conservatively in copy-all actions.
- Support/debug raw export is out of V1 unless permission and redaction contracts are explicit.

## 11. API And Contract Requirements

### 11.1 Required V1 Contracts

V1 needs these contracts to avoid guessing:

- Accepted Team run owner envelope with `ownerMemberId`, `publishedServiceId`, `runId`, `actorId`, `commandId`, and `correlationId`.
- Minimal owner resolver for cold-start reopen: `GET /api/scopes/:scopeId/teams/:teamId/runs/:runId/owner` or an equivalent typed query returning the immutable owner tuple.
- Durable run owner metadata for reopening.
- Team invocation discriminator for Team Detail latest work.
- Durable run summary.
- Durable run audit sufficient for ordered timeline and output.
- Redaction flags for sensitive fields that can appear in Information or Evidence. When a payload has no sensitivity contract, copy-all must omit raw payload bodies.

### 11.2 Existing Contracts To Reuse

- `POST /api/scopes/:scopeId/teams/:teamId/invoke/chat:stream`
- `GET /api/scopes/:scopeId/members/:ownerMemberId/runs`
- `GET /api/scopes/:scopeId/members/:ownerMemberId/runs/:runId`
- `GET /api/scopes/:scopeId/members/:ownerMemberId/runs/:runId/audit`
- Member run control endpoints only when typed capabilities expose them.
- `WorkflowRunEventEnvelope` event shapes.
- Workflow custom payloads for step request/completion, human input, approval, signal, and observed envelope.

### 11.3 Naming

Required frontend/API naming:

- `routeScopeId`
- `routeTeamId`
- `routeRunId`
- `entryMemberId`
- `ownerMemberId`
- `publishedServiceId`
- `draftWorkflowId`
- `workflowName`
- `actorId`
- `definitionActorId`

Forbidden naming:

- `workflowId` for any member, service, or run candidate.
- `serviceId` where the product means `publishedServiceId`.
- `teamRunActorId` unless a Team-owned run actor actually exists.
- Generic `id` variables carrying multiple identity candidates.
- Generic `Metadata` bags for stable run semantics.

### 11.4 Normalized Frame Model

Before rendering, raw realtime frames should normalize to typed UI frames:

- `RunLifecycleFrame`
- `StepInputFrame`
- `StepOutputFrame`
- `MessageFrame`
- `ToolFrame`
- `HumanInteractionFrame`
- `UsageFrame`
- `RawEvidenceFrame`

Each normalized frame must carry typed fields for status, step id, run id, preview text, waiting kind, error, and redaction state where applicable. Raw payload stays evidence, not control flow.

## 12. UI Requirements

### 12.1 Visual Direction

The cockpit should feel dense, calm, operational, and scan-friendly.

- Timeline is the center of gravity.
- Graph view appears only when authoritative layout exists.
- Active steps may use subtle motion.
- Failure and waiting states are visually obvious.
- Technical identities are compact and copyable in Evidence.
- Raw logs never become the first screen.

### 12.2 Required States

- No Team entry member.
- Entry member not workflow-capable.
- Entry member not bound.
- No verified Team runs.
- Entry member latest run exists but Team attribution is unavailable.
- Run accepted, durable summary pending.
- Durable summary available, audit unavailable.
- Audit available.
- Running with live frames.
- Running after refresh with durable data only.
- Live stream disconnected.
- Waiting without capabilities.
- Waiting with typed capabilities.
- Completed with output.
- Failed with error.
- Stopped with reason.
- Redacted evidence available.
- Forbidden/missing scope, Team, member, or run owner.

### 12.3 Accessibility

- Timeline rows are keyboard reachable.
- Status icons have accessible labels.
- Live updates do not steal focus.
- Waiting action forms are keyboard reachable when present.
- Copy and diagnostic toggles have explicit labels.
- Color is never the only status signal.

## 13. Success Metrics

Product success:

- A Team operator can identify the owner member, run status, current/last step, and latest output/error within 5 seconds.
- A reopened run remains inspectable after browser refresh.
- Team Detail does not mislabel member-only runs as Team work.
- A support user can copy a redaction-safe evidence bundle in one action.

Quality success:

- No route, API helper, or test fixture assumes `memberId === workflowId`.
- No production UI uses localStorage as authoritative Team run state.
- No query path performs event replay or projection priming.
- No application/projection/orchestration service adds process-local `runId -> context` fact state.
- Tests use distinct fixture ids:
  - `teamId = "team-alpha"`
  - `entryMemberId = "m-entry"`
  - `ownerMemberId = "m-alpha"`
  - `workflowId = "wf-alpha"`
  - `publishedServiceId = "svc-alpha"`
  - `runId = "run-alpha"`

## 14. Milestones

### M1: Identity And Contract Closure

- Approve immutable run owner tuple.
- Approve canonical route.
- Confirm accepted Team run owner envelope.
- Confirm minimal owner resolver for cold-start reopen.
- Confirm Team invocation discriminator or honest member-latest fallback.
- Confirm V1 workflow-member-only scope.

### M2: Durable Read-Only Cockpit

- Build cockpit for a verified `ownerMemberId + publishedServiceId + runId`.
- Render durable summary.
- Render ordered timeline from audit when available.
- Render output and evidence.
- Show accepted/durable pending/audit unavailable states.

### M3: Team Entry Integration

- Add Team detail live work strip.
- Start Team run and pin cockpit from accepted owner envelope.
- Avoid claiming Team work without Team invocation source.

### M4: Live Frame Enhancement

- Merge current-session live frames with durable data.
- Show live source state separately from run state.
- De-dupe or replace live rows when durable audit catches up.

### M5: Controls, Redaction, And Hardening

- Add V1.5 typed waiting capabilities if backend contracts exist.
- Harden redaction-aware Evidence copy/export beyond the V1 safe default.
- Add support/debug bundle only with explicit permission model.
- Add regression tests and CI guard coverage for identity and authority rules.

## 15. Acceptance Criteria

V1 is acceptable when:

1. Given a Team has a workflow entry member, Team Detail shows entry member identity and published workflow readiness.
2. Given a Team-started run has durable Team invocation context, Team Detail labels it as Team current/latest work.
3. Given only member history exists without Team attribution, Team Detail labels it as entry member latest run and does not call it Team work.
4. Given a Team run is accepted, the frontend receives an owner envelope containing `ownerMemberId`, `publishedServiceId`, `runId`, `actorId`, `commandId`, and `correlationId`.
5. Given a user opens `/scopes/:scopeId/teams/:teamId/runs/:runId` in a fresh browser session, the page resolves the immutable owner tuple through the V1 owner resolver before calling member-scoped run endpoints.
6. Given the Team entry member changed after a run, reopening the old run still uses the old run's `ownerMemberId`.
7. Given the owner member was rebound after a run, reopening the old run still uses the run's acceptance-time `publishedServiceId`.
8. Given durable summary is pending, the cockpit shows `accepted / durable pending` and does not infer failure from missing data.
9. Given durable audit is unavailable, the cockpit still shows summary/output/error from run summary and labels audit as unavailable.
10. Given durable audit is available, the cockpit rebuilds timeline from audit and marks durable source.
11. Given live frames arrive before durable audit, the cockpit shows them as live-only and later lets durable audit replace matching rows.
12. Given live stream disconnects, the cockpit marks live source disconnected without changing durable run status.
13. Given a secure/redacted field, Information and Evidence copy do not expose raw value.
14. Given payload sensitivity is unknown, copy-all excludes raw payload bodies.
15. Given waiting status in V1 Core, the cockpit does not render action buttons.
16. Given typed waiting capabilities in V1.5, the cockpit renders only those actions and treats receipts as accepted.
17. Given distinct fixture ids for Team, member, workflow, service, and run, route/API tests pass without identity aliasing.

## 16. Open Questions

1. What is the retention expectation for durable audit artifacts?
2. When should full Team-scoped multi-member run list/aggregation become required beyond the V1 owner resolver?
3. What permission model should unlock support-only raw evidence export?
4. How should non-workflow Team members appear in a future cockpit?
5. Which backend contract will carry redaction state for all payload classes that can appear in Evidence?

## 17. Review Log

### Round 1 With ChatGPT

Accepted changes from critique:

- Promoted immutable run owner tuple from implicit behavior to core requirement.
- Stopped using current `entryMemberId` as historical run authority.
- Added Team invocation discriminator requirement.
- Added V1 minimal owner resolver for cold-start reopen.
- Moved accepted run identity, audit completeness, redaction, and normalized frames out of "nice-to-have" language.
- Scoped V1 to durable read-only cockpit plus live enhancement.
- Added durable/live merge rules.
- Added typed waiting action capability model.
- Added Evidence redaction boundary.
- Removed open questions that contradicted earlier decisions.

Remaining items for later review:

- Whether Team-scoped multi-member run aggregation is needed after the V1 owner resolver.
- Whether support-only raw evidence export is needed after redaction-safe copy ships.

### Round 2 With ChatGPT

Accepted changes from critique:

- Added a V1 owner resolver so the canonical route can cold-start reopen a run without owner guessing.
- Separated minimal owner resolver from future Team run aggregation.
- Clarified that historical `publishedServiceId` is captured at acceptance time and must not be recomputed from current member binding.
- Defined unknown-sensitivity copy behavior: copy-all omits raw payload bodies.
- Reframed V1 Core as read-only and moved waiting controls to V1.5 unless typed capabilities already exist.

### Round 3 With ChatGPT

Final consensus:

- ChatGPT agreed this version can enter final PRD state.
- No blocking issues remained after adding the V1 owner resolver, unknown-sensitivity copy rule, read-only V1 Core boundary, and acceptance-time `publishedServiceId` rule.
- Shared conclusion: Team is the product context; member and `publishedServiceId` own execution; run inspection depends on an immutable owner tuple; live frames are session evidence; durable summary/audit are truth; V1 is a read-only durable cockpit with live enhancement.
