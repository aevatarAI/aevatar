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
| `workflow_started` | `workflowId`, `nodeCount` | A real `RUN_STARTED` from the active workflow editor invocation. A queued observation can also be confirmed by the same run's Activity record. |
| `workflow_finished` | `workflowId`, `status: success \| failed`, `duration` | Terminal SSE evidence, or an authoritative terminal Activity record for that exact run. `duration` is elapsed **milliseconds** from server timestamps; completion is deduplicated. |
| `sse_disconnect` | `workflowId`, `reason: unexpected_eof \| stream_error` | Unexpected termination of the stream for a confirmed started workflow. Normal terminal EOF, user cancellation and navigation are excluded. |
| `workflow_timeout` | `workflowId`, `timeoutMs` | Typed contract only; the current backend does not expose the run's configured timeout or a distinct timeout SSE contract. No guessed timeout event is emitted. |
| `sse_reconnect` | `workflowId`, `retryCount` | Typed contract only; the current invocation API uses POST SSE and exposes no safe resume/reconnect operation. Activity recovery is a readback, so it emits no reconnect event. |

`workflowId` is the workflow document's real ID. Member and published-service
identities must never be substituted. `nodeCount` counts the workflow nodes for
the invocation, not members in a team. Duration is a distribution of observed
finished workflows; P50 and P95 are percentiles, not an arithmetic mean.

These metrics cover Console editor invocations observed by the browser. They do
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
| Workflow success % | Series A: count `workflow_finished`, filtered to `status = success`. Series B: count `workflow_started`. Formula: `100 * A / B`. |
| Workflow duration P50 / P95 | Event: `workflow_finished`. Aggregate the numeric `duration` property at the 50th and 95th percentiles; show milliseconds. |
| SSE disconnect % | Series A: count `sse_disconnect`. Series B: count `workflow_started`. Formula: `100 * A / B`. |

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
