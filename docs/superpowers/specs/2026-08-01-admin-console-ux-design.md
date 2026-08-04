# Admin Console UX Design

- **Date:** 2026-08-01
- **Status:** Approved
- **Surface:** `/admin`, `/admin#/observatory`, `/workflow/observatory`

## Problem

The UI implies that the unified Workflow Observatory retained the viewing
capabilities of the former admin-local renderer, but the system removed
immersive observation when it deleted that duplicate renderer. The admin shell
also rebuilds entire views on refresh, while the canonical observatory preserves
only the run-list scroll position. Operators therefore lose context and pay for
avoidable iframe reloads.

This is an ownership and runtime mismatch. The canonical observatory owns run
selection, tabs, polling, immersive presentation, and its scroll state. The
admin shell owns module navigation and shell-level scroll state.

## Design

Keep one renderer and one data path. `/admin#/observatory` continues to embed
`/workflow/observatory`; no observatory cache, poller, or API client returns to
`admin.html`. The canonical page gains a compact workspace bar with honest
scope context, real manual refresh, admin tools, and an explicit immersive
action. Admin-only email/scope and full-run lookup move behind the compact tools
surface instead of occupying the primary canvas.

Immersive mode hides the suite navigation, run rail, and nonessential admin
controls while retaining the workspace context bar and full run detail. It is a
session preference, not shareable URL state. The embedded page notifies the
same-origin admin shell, which hides its rail, header, and account chrome.
`Escape` closes an active graph-node detail first; otherwise it exits immersive
mode without changing scope, run, filters, tab, or URL.

The canonical observatory records run-list and detail-canvas positions by the
canonical route (`scope + filters + run + tab`) in `sessionStorage`. Polling,
manual refresh, theme changes, and same-route renders preserve both positions.
Changing scope/filter/run/tab resets only the pane whose content identity
changed. Browser reload restores the matching route's positions.

The admin shell records scroll positions per hash route and restores them after
same-route renders, navigation back, and browser reload. Embedded suite views
are marked persistent: a same-route shell refresh updates shell chrome in place
instead of replacing the iframe, avoiding a second document load and preserving
the embedded view's state. Route identity changes still create the correct new
view.

## Structure and Performance

- `workflow-observatory.html` remains the sole run UI and owns observatory view
  state, rendering, refresh, and immersive behavior.
- `admin.html` owns only shell rendering, persistent embedded-view lifecycle,
  and generic shell scroll restoration.
- No dependency, build chain, API, or read-model change is introduced.
- Polling remains signature-gated; unchanged responses update only the live
  indicator. A manual refresh uses the same read-only request path.
- Existing static embedded assets and host configuration injection remain the
  deployment boundary.

## Accessibility and Responsive Behavior

The immersive and refresh controls have visible text, accessible names, focus
styles, and honest disabled state when no run is selected. Scope controls expose
pressed state. On mobile, normal mode stays list-then-detail; immersive mode
shows the detail canvas and context bar. Reduced-motion preferences continue to
disable decorative motion.

## Verification

Behavior tests execute the shipped JavaScript to cover route-keyed view-state
storage, selective scroll resets, immersive session state, parent-shell
messages, persistent iframe reuse, and real refresh behavior. Static asset and
architecture guards remain green. Browser verification covers normal and
immersive modes, Escape, polling/manual refresh, same-route navigation, browser
reload, desktop, and mobile widths.
