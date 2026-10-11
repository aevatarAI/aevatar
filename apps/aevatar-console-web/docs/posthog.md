# Console monitoring with PostHog

The Console initializes `posthog-js` when both `AEVATAR_POSTHOG_KEY` and
`AEVATAR_POSTHOG_HOST` are configured at build time. Missing configuration leaves
analytics disabled. A collector failure does not block sign-in or workflow runs.

## Browser configuration

Set these through the frontend deployment's existing environment configuration:

| Variable | Value |
| --- | --- |
| `AEVATAR_POSTHOG_KEY` | The PostHog project's **public project token**, suitable for browser capture. |
| `AEVATAR_POSTHOG_HOST` | The project's ingestion origin, for example `https://us.i.posthog.com` or `https://eu.i.posthog.com`. Use the host shown in that project's setup instructions. |
| `AEVATAR_POSTHOG_ENVIRONMENT` | `production` for the deployed Console. Development and staging must use distinct values. Defaults to the frontend build's `NODE_ENV`. |

Never place a PostHog **personal API key** in frontend variables, Umi `define`,
source files, or the browser bundle. Dashboard and alert setup uses the PostHog
interface and does not require management credentials in this repository.

Automatic collection includes LCP, INP and CLS through `capture_performance`,
unhandled errors through `capture_exceptions`, page views, and Session Replay.
In the target project's **Session Replay settings**, enable web recording and
check sampling, URL triggers and recording limits. Local SDK configuration alone
does not prove that the project has accepted recordings. INP requires user
interaction, and Web Vitals may arrive when the page becomes hidden or exits.

Time to First Byte (TTFB) is collected separately through `web-vitals` and the
`page_ttfb` event. PostHog's native Web Vitals allowlist and dashboard support
FCP, LCP, INP and CLS; they do not include a TTFB card. The Console enables the
requested LCP, INP and CLS, and uses a custom insight for TTFB.

Long Tasks are captured separately with the browser's native
`PerformanceObserver` after analytics initializes. A single document-wide
observer continues across SPA navigation and reports browser `longtask` entries
(tasks lasting approximately 50 ms or longer). Buffered collection also recovers
startup entries retained by the browser before initialization. Support depends
on the browser and is commonly available in Chromium. Browsers without
`longtask` support skip this collector; missing
events on those browsers do not establish that the page had no blocking.
Observer and collector failures cannot disable TTFB or other analytics.

Recordings mask inputs, text and content attributes while preserving structural
attributes and styles needed to reconstruct the layout. They block the workflow
code editor and `.ph-no-capture` elements, and exclude console logs and
request/response bodies and headers. URL sanitization removes query strings and
fragments, and OAuth callback events are discarded. Exception sanitization
redacts recognized credential fields and supported token patterns; it cannot
guarantee removal of every secret embedded in arbitrary error text. Inspect
representative replay and exception payloads during acceptance. Authenticated
users are identified by their existing opaque user ID; sign-out/account changes
reset the identity.

All captured events carry `app = aevatar-console` and `environment`. Filter on
both when sharing a PostHog project with another application.

## Event contract and current coverage

| Event | Properties | Producer |
| --- | --- | --- |
| `long_task` | `duration_ms`, `blocking_time_ms`, `start_time_ms`, `$current_url`, `$pathname` | Native Long Tasks observer, including buffered startup entries. One event per reported task, with matching sanitized URL/path; no raw entries, iframe attribution or content are sent. |
| `api_request` | `url`, `method`, `status`, `duration_ms`, `success`, `error_type` on failure | Shared `apiFetch` used by authenticated Console APIs, auth backend requests and NyxID connector requests. One event per network attempt; duration ends when response headers arrive or fetch rejects. |
| `page_ttfb` | `ttfbMs`, `navigationType`, `metricId`, `$current_url` | `web-vitals` reports the document's Navigation Timing after load. Time is in **milliseconds**; `$current_url` is the sanitized document navigation URL, preserved across later SPA navigation. No raw performance entries are sent. |
| `workflow_started` | `workflowId`, `nodeCount` | A real `RUN_STARTED` from the active workflow editor invocation. A queued observation can also be confirmed by the same run's Activity record. |
| `workflow_finished` | `workflowId`, `status: success \| failed`, `duration` | Terminal SSE evidence, or an authoritative terminal Activity record for that exact run. `duration` is elapsed **milliseconds** from server timestamps; completion is deduplicated. |
| `sse_disconnect` | `workflowId`, `reason: unexpected_eof \| stream_error` | Unexpected termination of the stream for a confirmed started workflow. Normal terminal EOF, user cancellation and navigation are excluded. |
| `workflow_timeout` | `workflowId`, `timeoutMs` | Typed contract only; the current backend does not expose the run's configured timeout or a distinct timeout SSE contract. No guessed timeout event is emitted. |
| `sse_reconnect` | `workflowId`, `retryCount` | Typed contract only; the current invocation API uses POST SSE and exposes no safe resume/reconnect operation. Activity recovery is a readback, so it emits no reconnect event. |

`workflowId` is the workflow document's real ID. Member and published-service
identities must never be substituted. `nodeCount` counts the workflow nodes for
the invocation, not members in a team. Duration is a distribution of observed
finished workflows; P50 and P95 are percentiles, not an arithmetic mean.

`api_request` reports the request origin and path without URL credentials, query
parameters or fragments. Methods are uppercase; `success` follows the HTTP
response's `ok` flag (2xx). HTTP failures retain the real status and use
`error_type = http_error`. Fetch rejections use `status = 0` and
`network_error`, `aborted` (AbortError), or `timeout` (TimeoutError). Error
messages, headers and request/response bodies are not captured. Browser network
errors do not distinguish CORS failures from connection failures.

Timing starts after any session restoration and ends at response headers, so it
does not include body download, JSON decoding, editor readiness or SSE stream
lifetime. A token refresh is a separate request. Later body/stream failures are
not reclassified as failed HTTP requests. Requests keep their original response,
body, signal, authentication and rejection behavior even if monitoring fails.
The wrapper does not replace global fetch or instrument PostHog's own traffic.
Existing disabled-analytics and OAuth-callback suppression rules still apply.

For `long_task`, `start_time_ms` is relative to the document's performance time
origin, and `blocking_time_ms` is the part of that task beyond 50 ms. Summing it
does not produce Lighthouse Total Blocking Time (TBT), which has a specific
measurement window. Tasks that started before observer registration use the
document's Navigation Timing URL, falling back to the URL at initialization when
Navigation Timing is unavailable. This is document-level startup attribution,
not a reconstruction of any earlier SPA routes. Later tasks use the current page
URL when the observer callback runs; a task spanning an SPA route change can be
attributed to the destination page. `$pathname` always matches the chosen URL.
Long Tasks indicate main-thread blocking, not API duration or page/editor
readiness, and the browser does not identify the responsible JavaScript function
through this event.

Workflow metrics cover Console editor invocations observed by the browser. They do
not cover schedules, channels, other clients, or a completion after the browser
closed. A failed start before `RUN_STARTED` is not a started workflow. Browser
measurements are therefore product/experience metrics, not a backend-wide SLO.

Timeout and reconnect producers are outside this frontend-only change. The
current contracts provide neither the actual `timeoutMs` nor a resumable stream;
repeating the invocation POST would create a new run.

## Configure dashboards and alerts in PostHog

After events arrive, create saved insights in the PostHog project and add them
to a dashboard. Filter every series to `app = aevatar-console` and
`environment = production`, with the same time window.

| Insight | Configuration |
| --- | --- |
| Long Task count / duration P95 | Event: `long_task`. Count events or aggregate numeric `duration_ms` at the 95th percentile; break down by `$current_url` and browser. Use a custom Product Analytics insight, not the native Web Vitals cards. |
| Long Task blocking time | Event: `long_task`. Sum numeric `blocking_time_ms` for a selected page and time window; compare alongside page views because total blocking grows with traffic. This is not Lighthouse TBT. |
| API latency P50 / P95 | Event: `api_request`. Aggregate numeric `duration_ms` at the 50th and 95th percentiles; break down by `url` and filter by `method` as needed. |
| API failure % | Series A: count `api_request` with `success = false`. Series B: all `api_request`. Formula: `100 * A / B`. Inspect failed-event counts separately by `error_type` to distinguish HTTP, network and cancellation failures. |
| Workflow success % | Series A: count `workflow_finished`, filtered to `status = success`. Series B: count `workflow_started`. Formula: `100 * A / B`. |
| Workflow duration P50 / P95 | Event: `workflow_finished`. Aggregate the numeric `duration` property at the 50th and 95th percentiles; show milliseconds. |
| SSE disconnect % | Series A: count `sse_disconnect`. Series B: count `workflow_started`. Formula: `100 * A / B`. |
| Document TTFB P75 | Event: `page_ttfb`. Aggregate numeric `ttfbMs` at the 75th percentile; show milliseconds and break down by `$current_url`. Exclude `navigationType = back-forward-cache`, whose zero values represent cached restores rather than network responses. |

Create the TTFB insight under **Product Analytics → Insights**, then add it to
the Console dashboard. It does not appear in **Web Analytics → Web Vitals**.
Use `environment = development` to inspect local verification traffic; saved
production insights intentionally exclude it. TTFB includes DNS, connection,
network and server waiting time for the HTML document. It is not the duration
of a workflow, an API request, editor readiness, or a client-side route change.
The observer supports Chromium, Firefox and Safari where Navigation Timing is
available; unavailable or invalid measurements produce no event.

Compare real page loads by opening each authenticated page URL directly. Native
LCP/INP/CLS and TTFB are scoped to document navigation, since experimental soft
navigation capture is not enabled. Clicking between SPA routes creates pageview
events but does not start a new document measurement. Use `$current_url` for
TTFB attribution, not the pathname at the time its callback runs. Development
builds and a few manual samples do not establish representative production P75.

Use **Error Tracking → Insights** for native frontend error analysis and
**crash-free sessions**, and **Web Analytics → Web Vitals** for LCP/INP/CLS.
The native crash-free card measures `(sessions - sessions with a crash) /
sessions`; it does not measure crash-free users. Apply app/environment filters
where supported, or use a dedicated Console project. A distinct-user crash-free
metric would require a separate definition and insight, not relabeling this
native card.

For alerts, create a saved **SQL insight** in the UI with the explicit guarded
conditions below. Do not attach a below-90 alert directly to the trend formula:
PostHog's formula evaluator returns zero for division by zero, and empty alert
results also evaluate as zero. This would trigger a false low-success alert in
an idle period. The SQL keeps the ratios null when there are no starts and emits
separate 0/1 breach values:

```sql
SELECT
    started,
    succeeded,
    disconnected,
    100.0 * succeeded / nullIf(started, 0) AS success_percent,
    100.0 * disconnected / nullIf(started, 0) AS disconnect_percent,
    if(started > 0 AND 100.0 * succeeded < 90.0 * started, 1, 0)
        AS success_breached,
    if(started > 0 AND 100.0 * disconnected > 5.0 * started, 1, 0)
        AS disconnect_breached
FROM (
    SELECT
        countIf(event = 'workflow_started') AS started,
        countIf(event = 'workflow_finished' AND properties.status = 'success')
            AS succeeded,
        countIf(event = 'sse_disconnect') AS disconnected
    FROM events
    WHERE properties.app = 'aevatar-console'
      AND properties.environment = 'production'
      AND timestamp >= toStartOfHour(now()) - INTERVAL 1 HOUR
      AND timestamp < toStartOfHour(now())
      AND event IN ('workflow_started', 'workflow_finished', 'sse_disconnect')
)
```

This query examines the **previous complete hour**. Open the saved insight's
**Monitor → Alerts → New alert** twice, selecting `success_breached` and
`disconnect_breached` respectively as the value column. For each, choose
**last row → Threshold → has value → more than 0**. Name them **Workflow success
below 90%** and **SSE disconnect above 5%**. Exactly 90% or 5% does not trigger;
an hour without starts does not trigger either alert.

Choose **Hourly** checks; the optional 10th minute allows some ingestion delay.
The SQL itself owns the window, so changing the check frequency does not change
the query's previous-hour scope. **Every 15 minutes** requires Boost or higher;
**Real time** checks approximately every two minutes and requires Scale or
Enterprise. Confirm the next scheduled check and project timezone in the UI.
Select the intended subscribed user for in-app notifications; email recipients
are a separate notification setting. Saving a rule does not prove that an email
destination is configured or that a notification was delivered.

Configure frontend error surges through **Error Tracking → Configuration →
Spike detection**, choosing its threshold, baseline multiplier and recipients
for the application's traffic. Native spike detection evaluates each issue's
exception volume; it does not measure a global exceptions-per-user ratio.

P50/P95 are percentiles, not an arithmetic mean. Count-based ratios follow the
selected event window: runs starting before the window and finishing inside it
can make success exceed 100%, while unfinished runs can lower it temporarily.
Use a suitable window and inspect the underlying runs before treating a breach
as an execution failure.

## Verify after deployment

- Confirm a workflow start and finish carry the correct workflow ID, published
  node count, status, millisecond duration and environment in the event explorer.
- Confirm unexpected stream termination produces one `sse_disconnect`; normal
  completion and leaving the page do not. Activity recovery is not a reconnect.
- Confirm a masked replay is available, a test exception appears in Error
  Tracking, and Web Vitals arrive after interaction and leaving/hiding the page.
  Not every browser session produces all three metrics.
- Hard-load a real authenticated Console page and confirm `page_ttfb` carries
  numeric `ttfbMs`, `navigationType`, `metricId` and its document URL without
  query strings or fragments. Confirm its custom P75 insight excludes bfcache
  restores and uses the intended environment.
- Review the dashboard, test the alerts including an idle period, and verify
  delivery to the configured recipients.

Project configuration and event ingestion must be verified in the actual
PostHog project. Local frontend tests alone do not establish those results.

- [Dashboard documentation](https://posthog.com/docs/product-analytics/dashboards)
- [Insight alerts](https://posthog.com/docs/alerts)
- [Formula division-by-zero behavior](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/posthog/hogql_queries/utils/formula_ast.py)
- [SQL alert no-data behavior](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/alerts/backend/evaluation/hogql.py)
- [Native crash-free sessions card](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/error_tracking/frontend/scenes/ErrorTrackingScene/tabs/insights/MetricTiles.tsx)
- [Web Vitals documentation](https://posthog.com/docs/web-analytics/web-vitals)
- [Native exception spikes](https://posthog.com/docs/error-tracking/spikes)
