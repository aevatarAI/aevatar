# Admin Observatory UI Design

- **Date:** 2026-07-24
- **Status:** Implemented; immersive mode and position restoration restored on 2026-08-01
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

- Default every caller, including an admin, to their own scope.
- Make all-scope observation an explicit and continuously visible admin mode.
- Give the selected run substantially more space without losing fast run
  switching in the normal layout.
- Make navigation, filtering, refresh, browser history, and shared links use
  the same URL-backed state.
- Use the existing read-only observatory API and its real filter contract.
- Preserve the backend console's restrained operational visual language.

## Non-Goals

- No new observatory API or read model.
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
  &tab=timeline|steps|diagnostics|logs|artifacts|graph
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
existing list endpoint. The status UI uses the backend values
`running/completed/failed/stopped`, with localized display labels. Local search
only filters the already loaded maximum of 100 summaries and displays the
visible/loaded count so it cannot be mistaken for a server-wide search.

Changing a server filter updates the URL and starts a new list request. A
monotonic request ID prevents an older response from overwriting a newer scope
or filter selection. Polling reuses the current URL-derived query.

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
  are compact; the Timeline/Steps/Diagnostics/Logs/Artifacts/Graph content is
  the primary visual surface.

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
