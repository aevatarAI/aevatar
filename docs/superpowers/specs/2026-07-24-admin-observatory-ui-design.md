# Admin Observatory UI Design

- **Date:** 2026-07-24
- **Status:** Implemented; operation-ledger semantics clarified on 2026-08-14
- **Surface:** `/admin#/observatory`
- **Owner:** Aevatar Mainnet Backend Console

## Problem

The current `/admin` observatory has three related product problems:

1. It treats admin authority as a default global observation scope. An admin
   enters the page with `scope=__all__`, although the expected default is the
   caller's own scope.
2. The run detail area is compressed by a full-width admin bar, a collapsed
   filter form, a fixed run list, the global rail, and the page header. Long
   timelines, logs, diagnostics, and step traces are difficult to inspect.
3. Navigation and filtering do not share one state model. Several inputs are
   visual only, schedule links write query parameters that the observatory does
   not consume, and Fleet run links force the observatory into all-scope mode.

In product terms, the UI implies "admin means global observer," while the
system and user expectation are "admin means the caller may explicitly widen
their observation scope." This is a placement, ownership, and mental-model
mismatch. Observation scope belongs to the observatory query context, not to
the caller's role badge.

## Goals

- Present every accepted workflow request/run as one independently selectable
  trace container. The display number is a reading aid; `runId` remains the
  stable container identity used for selection, refresh, and detail lookup.
- Inside the selected container, present an ordered operation ledger in which
  each Input, model response, and tool call is independently selectable, with a
  three-lane `Input / Model / Tools` duration overview over the same records.
- Preserve the original Timeline as the complete workflow-event view and add
  Trajectory as a separate detail tab; operation presentation must not replace
  or reduce Timeline information.
- Default every caller, including an admin, to their own scope.
- Make all-scope observation an explicit and continuously visible admin mode.
- Give the selected run substantially more space without losing fast run
  switching in the normal layout.
- Make navigation, filtering, refresh, browser history, and shared links use
  the same URL-backed state.
- Use the existing read-only observatory API and its real filter contract.
- Preserve the backend console's restrained operational visual language.

## Non-Goals

- No parallel observatory API or browser-owned durable read model. Typed
  Model/Tool operations extend the existing run-report/detail projection path;
  facts absent from that contract remain explicitly unavailable.
- No change to authorization or the `__all__` backend sentinel.
- No migration to `apps/aevatar-console-web` or a new frontend build chain.
- No redesign of unrelated `/admin` modules.
- No server-side full-text run search. Local search is explicitly limited to
  the loaded result page.

## Architecture Boundary

The production UI remains the checked-in embedded asset:

```text
src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
```

The page continues to call the existing read-only endpoints under
`/api/workflow/observatory`. The implementation may add small, focused state
and URL helpers inside the asset, but it must not introduce a second data path,
query-time projection work, or process-local business facts.

Static asset contract tests remain in:

```text
test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
```

Canonical behavior is documented in `docs/canon/backend-console.md`.

## Observation Scope Semantics

The UI has three resolved scope modes:

| URL value | Product meaning | List request |
|---|---|---|
| `scope=mine` or omitted | Caller's own scope | `/runs` with no `scope` query |
| `scope=all` | Explicit admin all-scope mode | `/runs?scope=__all__` |
| `scope=<scopeId>` | Explicit, exact scope | `/runs?scope=<scopeId>` |

`mine` is the default for every caller. Admin status only controls whether the
`all` and exact-scope controls are available. It never changes the default.

The header uses a segmented control labeled `我的 scope` and `所有 scope`.
When an admin selects all scopes, the header persistently identifies the page
as an admin global observation mode and offers a direct return to `我的 scope`.
An exact scope displays that scope ID and a direct return action.

Email/scope resolution and full-run-ID lookup move into a compact admin tools
menu. They no longer occupy a red, full-width toolbar. Cross-scope access
errors remain explicit and never fall back to broader data.

## URL State Contract

Observatory state is encoded in the existing hash query:

```text
#/observatory
  ?scope=mine|all|<scopeId>
  &status=running|completed|failed|stopped
  &origin=<csv>
  &definition=<csv>
  &schedule=<csv>
  &from=<ISO-8601>
  &to=<ISO-8601>
  &run=<runId>
  &tab=timeline|trajectory|steps|diagnostics|logs|artifacts|graph
```

Empty/default values are omitted from generated links. Parsing accepts only
known status and tab values. Invalid timestamps are ignored and surfaced as
invalid UI values rather than sent to the API. Unknown query keys remain
untouched only if they belong to another module; observatory-generated links
contain only the canonical observatory keys above.

The URL is the authority for scope, server filters, selected run, and selected
tab. Browser refresh and back/forward navigation restore the same view.
Immersive mode is intentionally not shareable URL state; it is a local viewing
preference stored in `sessionStorage`.

## Navigation Rules

Navigation builders preserve the identity known at the source:

- A schedule link navigates with `schedule=<scheduleId>` and keeps the current
  observation scope unless its source provides a more exact scope.
- A Fleet run link navigates with `run=<runId>` and the run's exact
  `scope=<scopeId>`. It must not force `scope=all`.
- Studio and Skills links include `run=<runId>` and any exact scope already
  present in their response or local record.
- A manually entered full run ID may be resolved through the admin run-detail
  endpoint without widening the list to all scopes.
- A selected deep-linked run that does not match the active list filters stays
  pinned above the filtered results and is labeled `不在当前筛选结果中`.
  The page must not silently select the first filtered row instead.

When the selected run's detail reveals its owning scope, the UI may display
that fact, but it does not silently rewrite an intentional `mine`, `all`, or
exact-scope selection.

## Filter Behavior

The compact filter bar contains:

- local search for workflow name or full run ID;
- one server-side status value;
- CSV-capable origin, definition, and schedule filters;
- from/to datetime controls;
- active filter chips with individual removal and one clear-all action.

`status`, `origin`, `definition`, `schedule`, `from`, and `to` are sent to the
activity-run feed endpoint. The status UI uses the backend values
`running/completed/failed/stopped`, with localized display labels. Local search
only filters the summaries already loaded into the rail and displays the
visible/loaded count so it cannot be mistaken for a server-wide search. The
first page contains the newest 100 trace containers; when `hasMore` and
`nextCursor` are present, the rail offers an explicit “load earlier” action and
keeps the loaded/total coverage visible.

Changing a server filter updates the URL and starts a new list request. A
monotonic request ID prevents an older response from overwriting a newer scope
or filter selection. Polling reuses the current URL-derived query. Cursor
loading is serialized against polling; a refreshed newest window overwrites
matching `runId` rows but retains every already loaded older identity, so new
head insertions cannot create a gap at the server's 500-row page boundary.

## Layout

The approved direction is **collapsible run rail plus large detail canvas**.

### Normal Mode

- A compact observatory header owns scope, refresh, admin tools, and the
  immersive action.
- Common filters remain visible in one dense toolbar. Less common fields open
  from the filter menu; active values remain visible as chips after it closes.
- The run rail is slightly wider than the current 320 px rail and shows run
  name, shortened ID, update time, definition, status, and origin without
  changing row height on hover or refresh.
- The rail can be collapsed independently. Its collapsed state is a local UI
  preference and does not affect the URL.
- The detail canvas owns the remaining width. Run identity and source facts
  are compact; the Timeline/Trajectory/Steps/Diagnostics/Logs/Artifacts/Graph
  content is the primary visual surface.

### Immersive Mode

Immersive mode is entered only through an explicit icon-and-text action. The
choice is remembered for the browser session.

It hides the global navigation rail, global page header, run rail, and
nonessential controls. A compact observatory context bar remains, containing
the current scope, selected run, live/refresh state, and exit action. The run
identity, tabs, and tab content use the full remaining viewport.

`Escape` exits immersive mode. It does not clear filters, change the selected
run, close unrelated dialogs first, or mutate the URL. If a modal/menu owns
Escape at that moment, that overlay closes before immersive mode exits.

### Responsive Behavior

At mobile widths, the page keeps a vertical list-then-detail structure. The
run rail becomes a bounded-height list above the detail and immersive mode
removes it. Controls wrap without text overlap; scope mode and active filters
remain identifiable. Desktop-only side-by-side density is not forced onto
mobile.

## Run Detail Behavior

The run rail lists request/run trace containers. Each row represents one
authoritative workflow run and exposes enough request context to distinguish
nearby calls without opening them: workflow, safe input summary when available,
status, origin, update time, duration/current step, and a shortened run ID. The
row is not an atomic trajectory event. Selecting it opens the container's
detail workspace; timeline events, trajectory operations, steps, diagnostics,
logs, artifacts, and graph remain children of the selected container rather
than parallel top-level records.

Timeline and Trajectory are sibling tabs with different owners. Timeline is the
default and keeps the original complete materialized workflow-event
presentation: timestamp, stage, message, actor/agent, step, event type, and
event data remain available. This does not imply that every runtime-specific
step output is duplicated into Timeline. Adding Trajectory must not filter,
summarize, replace, or otherwise reduce that event view.

The Trajectory tab owns the chronological operation ledger. It emits a distinct
selectable record for the captured run input, each model response, and each tool
call; nested tool work may remain linked beneath its owning tool operation. A
fixed overview above the ledger has three aligned lanes: `Input`, `Model`, and
`Tools`. Every overview marker/bar and ledger row refers to the same stable
operation identity. The run/request identity is used only to own and group those
operations; it must not collapse a multi-step model/tool exchange into one
summary row.

Clicking an operation opens a local inspector rather than replacing the run
detail route. Input records expose captured content and source. Model records
expose output, provider/model, usage, and timing when recorded. Tool records
expose payload, result, call-time schema, and timing when recorded. Status and
error state remain separate from the operation kind. Request-level status,
options, cumulative usage, and navigation remain a container summary, not a
substitute for operation details.

The overview renders duration bars only from real operation timestamps. A
recorded start without a recorded completion is a start marker or running
record, not a growing duration bar. When either timing fact is absent, Duration
is `unavailable`; the UI must not derive it from `updatedAtUtc`, polling time,
adjacent event timestamps, list order, or the parent run duration.

### Current Operation Contract And Gaps

The activity-run projection provides the request/run container list. Separately,
committed role progress is copied into run-owned
`WorkflowRuntimeOperationRecordedEvent` facts and materialized into the existing
run-report operation read model. The Observatory detail response exposes those
typed Model/Tool operations with stable operation/session identity, progress
sequence, kind, actual start/completion timestamps, and captured operation facts:
model/provider, input summary, model-visible tool names, output/reasoning,
finish reason, usage and errors for Model; tool call/name, arguments, result and
errors for Tool. The Trajectory tab consumes this operation collection directly;
it does not classify Timeline prose. Workflow steps remain in their dedicated
workflow-oriented view and do not become Model or Tool operations.

The remaining DSH-equivalence gaps are explicit:

- Input comes from the committed run input but has no independent typed
  start/completion lifecycle, so it has no operation Duration bar;
- a Model or Tool duration is available only when both typed lifecycle timestamps
  were recorded for that exact operation;
- the contract carries model-visible tool names but not the request-time tools
  catalog with per-tool schemas;
- the contract does not expose separate TTFT and decoding timestamps.

Missing Duration, model, usage, tools-catalog, or schema values are shown as
`unavailable`. The UI must not parse Timeline/step prose, actor IDs, current-step
summaries, `updatedAtUtc`, or generic-bag key spellings to fill those fields.
Query-time event replay or browser-side durable reconstruction remains outside
this design.

The rail reads the paged activity-run projection, while Timeline and Trajectory
both arrive in the selected run detail response and graph keeps using its
existing endpoint. The page polls the current rail and only the selected detail
approximately every three seconds while visible. The live indicator describes
this near-live projection refresh honestly; it does not
claim that a transport ACK is committed or that the read model is strongly
consistent. Incoming rows do not change an explicit selection. A selected run
that is briefly absent from the eventually consistent detail projection remains
eligible for later polling, so it can recover without a manual page refresh.

The detail header shows workflow name, honest status, full run ID, scope,
definition, origin, optional schedule ID, state version, and last update.
Copy and overflow actions use familiar icons with accessible labels/tooltips.

Metrics retain stable dimensions. Tabs do not resize when counts appear.
Timeline and log text use the available width and preserve long content without
covering adjacent controls. Polling keeps cached detail visible, preserves the
current tab and scroll position, and shows refresh errors as nonblocking stale
data notices.

Detail and graph endpoints follow the resolved observation intent:

- `mine`: normal detail/graph endpoints with no scope query;
- exact scope: normal detail/graph endpoints with `scope=<scopeId>`;
- all-scope list selection or unknown-scope manual run lookup: admin detail and
  graph endpoints.

Admin authority alone is not a reason to use the admin detail endpoint.

### Failure Evidence Contract

Failure evidence belongs to the selected run's committed current-state and
run-report projections. The browser does not reconstruct failures from prose,
and the query service does not replay events or prime projections. For a failed
run, the first detail viewport summarizes every available failure source while
the Diagnostics, Steps, Trajectory, Timeline, and Logs tabs retain their native
detail:

- compilation and current-state terminal errors;
- the activity projection's committed failure summary (not proof of the first failed attempt);
- run-report final error and failed-step error/output;
- failed model/tool operations and their captured result;
- recovery eligibility blockers; and
- unavailable or version-mismatched detail sections.

Failed-step output is a dedicated typed run-report field because native workflow
steps do not necessarily emit a role reply or tool-call Timeline record. It is
sanitized before persistence and bounded to 64 KiB of UTF-8, preserving both the
beginning and end so terminal stderr remains useful. A typed truncation flag is
the only authority for telling the operator that middle content was omitted.
Each per-file result `output`/`error` is independently sanitized and bounded to
8 KiB of UTF-8; vote-agreement decision `output`/`reason` keeps the 64 KiB
UTF-8 limit. Every bounded field has its own typed truncation flag; an upstream
`true` flag remains `true` across projection materialization and report export.
Failure outcome, recovery failure kind, retry disposition, per-file results,
and vote-agreement decisions remain typed fields rather than Timeline metadata
keys. A per-file result collection retains at most 32 entries using a
deterministic head/tail subset (first 16 and last 16). Its typed
`source_result_count` and `results_truncated` fields distinguish a complete
collection from a bounded one. When an upstream producer reports truncation but
does not know the original count, `source_result_count = 0` means unknown; the
query and browser must not present it as an exact count.

A retry request moves the complete previous failed attempt into the typed
`latest_failed_attempt` sub-message before resetting every current-attempt
completion, failure, branch, suspension, approval, and usage field. The
snapshot retains the failed request's own type, target role, parameters,
requested/completed timestamps, and evidence. A step whose current `outcome`
is `waiting` reads failure details only from that snapshot, so a retry's request
identity is never attributed to the previous failure. The snapshot is cleared
when the current attempt completes; a subsequent retry snapshots that newer
failure in turn.

The detail contract exposes the run-report `reportVersion`. Failed, timed-out,
and stopped runs whose report version is missing or older than `3.1` receive a
separate `failure_evidence_schema_legacy` diagnostic. The warning says that
dedicated failure fields may be unavailable and that background repair or
reprojection is required; the query path does not replay or silently upgrade a
legacy `3.0` report. This schema warning is independent of section state-version
availability or mismatch diagnostics.

All failure text and nested structured details are redacted before they cross
the projection boundary. Credential-shaped field names, including compound
names ending in singular `token`, `secret`, `password`, `credential`, or
`api_key`, are treated as sensitive. The console may expose all safely captured
diagnostic evidence; it must never offer a raw/unredacted fallback.

## Accessibility

- Scope selection, filter menu, filter chips, run rows, tabs, rail collapse,
  and immersive mode are keyboard operable.
- Segmented scope buttons expose pressed/selected state.
- The selected run and active tab expose their state semantically.
- Icon-only actions have accessible names and visible focus.
- Status never relies on color alone.
- Dynamic list counts and load failures are announced without repeatedly
  announcing every polling tick.

## Error and Empty States

List loading, empty results, forbidden scope, request failure, missing run, and
detail refresh failure are distinct states. Existing data remains visible
during background refresh. A forbidden all/exact-scope request keeps the user
in the chosen mode so the error is honest; a clear action returns to `mine`.

An empty filtered list names the active filters and offers clear-all. An empty
own-scope list does not encourage switching to all scopes; it simply reports
that the caller has no matching runs.

## Testing

Static asset tests must fail before implementation and cover:

- an admin's default list request omits `scope=__all__`;
- all-scope mode is reached only through explicit URL/control state;
- URL parsing and request construction for every supported filter;
- status uses one backend-supported value;
- schedule navigation is consumed by the list request;
- Fleet/run navigation carries exact scope and never forces all scopes;
- `mine`, exact scope, and all/unknown run lookup choose the correct detail and
  graph endpoints;
- a deep-linked run outside filters stays pinned and selected;
- tab/scope/run state restores from the URL;
- immersive mode enters explicitly, persists in `sessionStorage`, and exits
  with Escape;
- stale list responses cannot replace a newer scope/filter result;
- cached detail remains visible during polling.
- the original Timeline remains the default, retains its complete event detail,
  and is not replaced by the Trajectory renderer;
- `tab=trajectory` selects the independent Trajectory tab and survives route
  restoration;
- a run/request row is treated as a container while Input, Model, and Tool
  operations remain independently selectable;
- the three overview lanes and ledger rows share operation identities;
- typed Model/Tool operation facts are consumed from the detail contract rather
  than reconstructed from Timeline or workflow-step prose;
- Input and other missing operation timing or tools-catalog facts render as
  unavailable and are never inferred from refresh or update timestamps;
- failed native-step output is read from the typed step failure field, not
  assumed to exist in Timeline;
- failure output truncation is visible and follows the projection's typed flag;
- waiting retries preserve and label the latest failed-attempt evidence without
  being classified as current failed steps;
- bounded per-file collections expose both source count and collection-level
  truncation, including the explicit unknown-count case;
- legacy report schema warnings and `reportVersion` remain present in the
  copied issue payload;
- compilation, activity-failure-summary, failed-operation, recovery-blocker, and section
  version diagnostics remain present in the copied issue payload and Logs; and
- credential-shaped nested failure fields are redacted before display.

Visual verification covers `1440x900`, `1920x1080`, `1024x768`, and `390x844`.
It checks normal and immersive modes, expanded filters, long run IDs, long log
lines, empty/error states, and text/control overlap.

## Documentation and Verification

`docs/canon/backend-console.md` will record the default-scope rule, URL state
contract, exact-scope navigation rule, and admin detail endpoint selection.

Required verification includes the focused capability test project, static
asset guard, test stability guard, documentation lint, and the repository's
architecture guards. Because the change remains a checked-in static asset,
there is no frontend package build step for `/admin`.
