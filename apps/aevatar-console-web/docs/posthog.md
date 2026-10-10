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
source files, or the browser bundle. The provisioning script below uses a
different, operator-only environment.

Automatic collection includes LCP, INP and CLS through `capture_performance`,
unhandled errors through `capture_exceptions`, page views, and Session Replay.
In the target project's **Session Replay settings**, enable web recording and
check sampling, URL triggers and recording limits. Local SDK configuration alone
does not prove that the project has accepted recordings. INP requires user
interaction, and Web Vitals may arrive when the page becomes hidden or exits.

Recordings mask all inputs and text, block the workflow code editor and
`.ph-no-capture` elements, and exclude console logs and request/response bodies
and headers. URLs lose query strings and fragments. OAuth callback events are
discarded. This retains navigation, timing and interaction evidence while
protecting workflow inputs and output content. Authenticated users are identified
by their existing opaque user ID; sign-out/account changes reset the identity.

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

To implement the remaining two producers, the frontend needs a typed run timeout
contract containing the actual `timeoutMs`, and a resumable observation contract
with a stable run identity/cursor. Repeating the invocation POST would create a
new run and must not be used as an SSE retry.

## Dashboard and alert provisioning

Run the script from `apps/aevatar-console-web/`. Node 20 or later is sufficient;
there are no additional script dependencies.

```bash
node scripts/posthog-provision.mjs
```

The default is a local JSON plan. It needs no credentials and performs **no
network requests**. Review that plan before applying it to a project. To select
another environment, set `POSTHOG_ENVIRONMENT`; it must match the browser's
`AEVATAR_POSTHOG_ENVIRONMENT` and contain 1–20 letters, digits, underscores or
hyphens.

For application, supply the following through an operator shell or secret
manager, then run the explicit command below. Do not copy the personal key into
the frontend's dotenv files or commit it.

| Operator variable | Required value |
| --- | --- |
| `POSTHOG_MANAGEMENT_HOST` | The project's **management** origin, such as `https://us.posthog.com` or `https://eu.posthog.com`; not the `*.i.posthog.com` ingestion origin. |
| `POSTHOG_PROJECT_ID` | Numeric project ID from the project's URL/settings. |
| `POSTHOG_PERSONAL_API_KEY` | A personal key authorized to read/write dashboards, insights and alerts in that project. |
| `POSTHOG_ALERT_USER_IDS` | Comma-separated numeric PostHog user IDs for the explicitly selected recipients. At least one is required. |
| `POSTHOG_ENVIRONMENT` | Optional; defaults to `production`. |

```bash
node scripts/posthog-provision.mjs --apply
```

The script reads all current dashboards, insights and alerts before writing.
Managed dashboard/insight tags and alert names make sequential reruns update the
same resources. Duplicate ownership markers cause an error before the first
write. Existing membership of an insight in another dashboard is preserved.
Run one provisioner at a time; the API does not provide a cross-resource
transaction, and rerunning after a partial failure reconciles the resources that
were already created. The script does not delete resources. Applying again sets
the managed alerts' enabled state and subscriber list to this configuration.

The dashboard is named **Aevatar Console (production) — Workflow health** by
default. It contains four cards:

| Card | Exact calculation | Window |
| --- | --- | --- |
| Workflow success % | `100 × count(workflow_finished, status=success) / count(workflow_started)` | Rolling 24 hours |
| Workflow duration P50 (ms) | `quantile(0.5)` of `workflow_finished.duration` | Rolling 24 hours |
| Workflow duration P95 (ms) | `quantile(0.95)` of `workflow_finished.duration` | Rolling 24 hours |
| SSE disconnect % | `100 × count(sse_disconnect) / count(workflow_started)` | Rolling 24 hours |

Both ratios are null when there are no starts; duration cards are null when
there are no finishes. All event counts use the same app, environment and time
window. Counts follow event timestamps, so a run starting before the window and
finishing inside it can make a ratio exceed 100%. The dashboard preserves the
requested count-based definition without silently replacing it with a funnel,
cohort or a clamped ratio. Long-running workflows can also lower a short-window
success ratio temporarily. Use Activity to confirm the underlying runs.

The SQL in these cards owns its time window. Changing a dashboard date picker
does not rewrite these SQL expressions; edit the saved query or change the
provisioner if a different window is needed.

Two additional saved SQL insights provide alert evidence. They examine the
**previous complete clock hour**, and the corresponding alerts are evaluated
hourly:

| Alert | Breach condition |
| --- | --- |
| Workflow success below 90% | At least one start and `100 × successful finishes < 90 × starts`. Exactly 90% does not fire. |
| SSE disconnect above 5% | At least one start and `100 × disconnects > 5 × starts`. Exactly 5% does not fire. |

The saved evidence includes the raw counts, ratio and an `alert_breached` value
of 0 or 1. PostHog monitors that value with an absolute upper bound of 0. This
explicit guard matters: PostHog's SQL alert evaluator treats null/empty values
as zero, which would otherwise generate a false low-success alert during an
idle hour. There is no additional minimum-volume suppression beyond one start.
Late-ingested events and runs spanning hour boundaries remain limitations of
this event-window definition. Check the project's timezone and scheduling
preferences in the alert UI before enabling faster or narrower windows.

## Default product panels and frontend error spike alerts

Use PostHog's **Error Tracking** views for frontend exception/error rate and
crash-free users, and **Web Analytics → Web Vitals** for LCP, INP and CLS. Keep
their native calculations and drilldowns; do not substitute a ratio of custom
workflow events for the default error or performance metrics. Apply the
`aevatar-console`/production filters supported by those views, or isolate the
Console in its own project when the native view cannot filter that dimension.

Configure native exception spikes separately because the notification
destination and permissions belong to the PostHog project:

1. Open **Error Tracking → Configuration → Spike detection**.
2. Set an initial **Minimum threshold = 10** exceptions per 5-minute bucket,
   **Multiplier = 3**, and **Snooze duration = 60 minutes**. These are explicit
   starting operational choices; adjust them to the application's traffic.
3. Add a spike notification and select the intended destination/recipients.
   Confirm app/environment filtering or use a dedicated production project.
4. Use the destination's test function, confirm receipt, then save/enable it.

Native spike detection measures each issue's exception volume against its
rolling baseline. It is not a global exceptions-per-user ratio. This catches
the requested frontend error surge using PostHog's built-in error monitoring;
its default error-rate/crash-free panels remain available for overall impact.
Do not mark this step complete until a real project has saved the rule and a
recipient has confirmed a test notification. The provisioning script prints
this remaining native configuration step and does not claim to create it.

## Project acceptance checks

After an authorized deployment with a real public project token:

1. Run a workflow from the Console editor and confirm one `workflow_started`
   and one `workflow_finished` with the correct distinct workflow identity,
   node count, status, millisecond duration and environment in PostHog's event
   explorer. Inspect both a success and a real failure.
2. Interrupt an active stream in a controlled development/staging session and
   confirm one `sse_disconnect`. Recovering via Activity may finish the run's
   measurement; it must not create an `sse_reconnect` event.
3. Confirm a masked Session Replay exists, no workflow text/inputs or OAuth
   callback parameters are visible, and a real test exception appears in Error
   Tracking. Source-map upload is a separate deployment integration if readable
   production stacks are required.
4. Interact with the page, then leave/hide it and confirm LCP/INP/CLS in the
   native Web Vitals view. Not every browser session produces all three.
5. Open the provisioned dashboard, inspect saved alert queries/check history,
   confirm the zero-start guard and recipient list, and complete the native
   spike notification test. Verify the next scheduled alert check is present.

Without a project, credentials and authorized deployment, local checks prove
code/query-plan generation and provisioning behavior only. They do not prove
event ingestion, replay availability, actual HogQL execution, enabled remote
alerts or delivered notifications.

## API references and local verification

The provisioner uses PostHog's current dashboard/insight REST APIs and the
official alert contract. It is not a claim of compatibility with every older
self-hosted PostHog release. An API error stops the script without logging a
response body or a credential; inspect project permissions/version before
retrying.

- [Dashboard documentation](https://posthog.com/docs/product-analytics/dashboards)
- [Query API](https://posthog.com/docs/api/queries)
- [Insight alerts](https://posthog.com/docs/alerts)
- [Native exception spikes](https://posthog.com/docs/error-tracking/spikes)
- [Alert serializer contract](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/alerts/backend/presentation/views/alert.py)
- [Alert API integration examples](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/alerts/backend/tests/api/test_alert.py)
- [SQL alert no-data semantics](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/alerts/backend/evaluation/hogql.py)
- [Insight/dashboard association contract](https://github.com/PostHog/posthog/blob/fb029ea7e37f18963007287957ca15449ed6188f/products/product_analytics/backend/presentation/insight.py)

Focused script checks, from the frontend directory:

```bash
node --check scripts/posthog-provision.mjs
node --test scripts/posthog-provision.test.mjs
node scripts/posthog-provision.mjs
```

The tests exercise repeat application without duplicate resources, preservation
of dashboard memberships, abort-before-write on duplicate ownership, and
rejection of pagination that could forward a credential to another project or
origin. Full frontend tests, typecheck and production build belong to GitHub CI
under the personal incremental validation policy.
