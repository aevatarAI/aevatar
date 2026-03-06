# Metrics Baseline Plan

> Status: In progress on branch `feature/metrics-observability-plan`.

## 1. Purpose

Provide a low-cardinality, production-safe observability baseline for workflow runtime and API:

- Separate platform health metrics from user-perceived latency metrics.
- Support SSE/WS first-response analysis (TTFB-like) in addition to full request duration.
- Keep instrumentation generic, minimal, and layered.

## 2. Design Principles

- One meter per domain boundary: `Aevatar.Agents` for runtime, `Aevatar.Api` for API layer.
- Prefer low-cardinality labels over high-cardinality identifiers.
- Track both user-perceived latency (API) and backend processing overhead (runtime).
- Use OpenTelemetry OTLP export with Prometheus + Grafana as the default metrics display stack.

## 3. Anti-Patterns to Avoid

- Per-actor/per-session/per-command labels in metrics.
- Parallel metric systems with overlapping names and meanings.
- Multiple `Meter` objects sharing the same name across assemblies.
- Dashboard-only metrics that are not used in SLO/SLA decisions.
- `TypeUrl.Contains(...)` in src (blocked by CI guard).

## 4. Current Implementation Status

Implemented:

- OTLP metric export from `Workflow.Host.Api`.
- Collector-centered local stack (`docker-compose.observability.yml`) with Jaeger, Prometheus, and Grafana.
- Runtime metric cleanup and low-cardinality refactor:
  - removed high-cardinality labels (`agent_id`, `publisher_id`).
  - removed low-value instruments (`RouteTargets`, `StateLoads`, `StateSaves`, `HandlerDuration`).
- Runtime metrics emitted from both Local and Orleans paths.
- API metrics for request count and full duration (meter `Aevatar.Api`) on all interaction endpoints:
  - `POST /api/chat`
  - `POST /api/workflows/resume`
  - `POST /api/workflows/signal`
  - command-style HTTP request path
  - `GET /api/ws/chat`
- API first-response duration metric for streaming paths and WS parse error responses.
- Unified instrumentation scopes that compose tracing + logging + metrics:
  - `EventHandleScope` (runtime): single scope drives Activity span, log scope, and metrics recording.
  - `ApiRequestScope` (API): single scope drives stopwatch, result classification, and first-response tracking.
  - Eliminates duplicate Stopwatch and independent error-tracking across tracing/metrics.
- `OperationCanceledException` consistently classified as `result=ok` across HTTP and WebSocket paths.
- `ChatWebSocketRunCoordinator` decoupled from metrics (no metrics return value; scope passed from caller).
- Histogram views are configured explicitly for AI-oriented request latency and runtime event latency buckets.
- Grafana dashboard panels for:
  - health/error ratio
  - runtime/API throughput and latency
  - window totals (`increase`)
  - first-response vs full-response comparison

Pending:

- Add explicit SLO panel with thresholds and status coloring.
- Tune alert thresholds with production baselines after traffic observation.
- Tune histogram bucket boundaries with production latency samples if workload profile changes.

## 5. Metric Contract (Current)

### 5.1 Runtime Metrics (Meter: `Aevatar.Agents`)

| Name | Type | Labels | Meaning |
|---|---|---|---|
| `aevatar_runtime_events_handled_total` | Counter | `direction`, `result` | Number of handled runtime events |
| `aevatar_runtime_event_handle_duration_ms` | Histogram | `result` | Event handling duration (platform overhead) |
| `aevatar_runtime_active_actors` | UpDownCounter | none | Active actor count |

### 5.2 API Metrics (Meter: `Aevatar.Api`)

| Name | Type | Labels | Meaning |
|---|---|---|---|
| `aevatar_api_requests_total` | Counter | `transport`, `result` | API request volume |
| `aevatar_api_request_duration_ms` | Histogram | `transport` | End-to-end request duration |
| `aevatar_api_first_response_duration_ms` | Histogram | `transport`, `result` | Time to first response frame/ack |

`transport` values: `http`, `ws`

`result` values: `ok`, `error`

### 5.3 First-Response Semantics (Explicit Contract)

`aevatar_api_first_response_duration_ms` is defined as "time from request accepted to first response frame/ack/error sent to client".

- `http` path:
  - recorded on the first response signal sent to the client:
    - run-context bootstrap frame (`aevatar.run.context`), or
    - first streamed output frame (`emitAsync`) if no bootstrap frame was written first.
  - not recorded for prompt validation early return (400) because no response stream frame is produced.
- `ws` path:
  - recorded on first outbound message among `command ack`, `agui event`, or `command error`.
  - if the request fails before any websocket message is sent, no first-response metric is recorded.

This contract intentionally tracks "first observable response signal" rather than "request finished".

### 5.4 Request-Duration Tagging Trade-Off

`aevatar_api_request_duration_ms` intentionally omits the `result` label. This is a deliberate cardinality and dashboard-simplicity trade-off:

- error ratio is derived from `aevatar_api_requests_total{result=...}`.
- latency panels focus on user wait time split by `transport`.
- adding `result` to the histogram would double API latency series without materially improving the primary SLO view.

If per-result latency distributions become operationally necessary later, introduce them as a separate histogram with an explicit need, rather than retrofitting the core baseline.

### 5.5 Cancellation Semantics

`OperationCanceledException` is treated as `result=ok` in the request metric. Client-initiated cancellation is not a service error; it reflects normal connection lifecycle behavior (e.g., user navigates away, timeout). This keeps the error ratio focused on genuine service-side failures.

## 6. Diagnostic Model

When AI is part of the core request path, end-to-end latency alone cannot diagnose health. The layered metric approach separates concerns:

| Metric | Answers | Normal Range |
|---|---|---|
| **error ratio** | Is the service stable? | < 1% |
| **first_response p95** | Is user-perceived responsiveness OK? | Acceptable TTFB |
| **request_duration p95** | Is total time reasonable? | High variance expected with AI |
| **runtime event duration p95** | Is platform overhead normal? | Should be consistently low |

Interpretation rules:

- High full latency + normal first-response + low error ratio → AI generation variance (normal).
- Rising error ratio + dropping throughput → service incident.
- Rising runtime event latency → platform bottleneck (investigate regardless of AI content).
- Full request duration minus first response → AI generation time (expected to vary).

### Why NOT split AI/core at the runtime event level

The runtime event layer processes individual envelopes. A single AI chat request fans out to many events (ChatRequest, TextMessageStart, N × TextMessageContent, TextMessageEnd). Only one event (the LLM call trigger) is slow; the rest are fast streaming relays. A `pipeline=ai|core` label at this level gives misleading distributions (p50 looks fast because most AI events are fast) and adds label cardinality without clear diagnostic value.

The correct separation is at the API level: `first_response_duration` captures the real AI-latency impact on user experience, and `request_duration` captures the total including AI generation.

## 7. Dashboard and Display

Grafana dashboard file:

- `tools/observability/grafana/provisioning/dashboards/aevatar-runtime-overview.json`

Panels (SLO section, default view):

1. SLO Read Guide (text)
2. Error Ratio — API and runtime (timeseries)
3. User Latency: First Response p95 (timeseries)
4. User Latency: Full Request p95 (timeseries)
5. API Request Latency p99 (timeseries)
6. Runtime Event Handle Latency p95/p99 (timeseries)

Panels (Runtime Diagnostics section, drill-down):

7. Runtime Diagnostics Guide (text)
8. Active Actors (stat)
9. Runtime Events — Self, Window Total (stat)
10. API Requests — Window Total (stat)
11. Runtime Self Events / API Request (stat)
12. Runtime Events Rate — Self, by result (timeseries)
13. API Requests Rate by result (timeseries)

Note: "Runtime Self Events / API Request" is computed only when window API request count is greater than 0; otherwise the panel is intentionally empty to avoid misleading inflation from background self events.

Local stack:

- `docker-compose.observability.yml`
- OpenTelemetry Collector OTLP HTTP ingest endpoint: `http://localhost:4318` (ingest only, no UI)
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000`
- Jaeger: `http://localhost:16686`
- Prometheus alert examples: `tools/observability/prometheus/alerts.yml`

## 8. Verification Checklist

- Prometheus target `aevatar-otel-collector` is `UP`.
- Collector-exposed metrics include:
  - `aevatar_runtime_events_handled_total`
  - `aevatar_api_requests_total`
  - `aevatar_api_first_response_duration_ms`
- No high-cardinality labels appear in metric series.
- No `pipeline` label in runtime metrics.
- Dashboard shows first-response vs full-response latency.
- `alerts.yml` loads successfully in Prometheus rule status page.

## 9. Metric Quick Reference (Runbook)

### 9.1 Core SLO Metrics

| Metric | PromQL (Reference) | Why it matters | Suggested alert threshold | Common misread |
|---|---|---|---|---|
| API error ratio (5m) | `sum(increase(aevatar_api_requests_total{result="error"}[5m])) / clamp_min(sum(increase(aevatar_api_requests_total[5m])), 1)` | Primary service stability signal for client requests | `> 1%` for 5-10 minutes | Counting 4xx as service error (current contract treats only 5xx as `error`) |
| Runtime event error ratio (5m) | `sum(increase(aevatar_runtime_events_handled_total{result="error"}[5m])) / clamp_min(sum(increase(aevatar_runtime_events_handled_total[5m])), 1)` | Runtime pipeline health signal | `> 1%` for 5-10 minutes | Comparing directly with API ratio without considering event fan-out |
| First response latency p95 | `histogram_quantile(0.95, sum by (le) (rate(aevatar_api_first_response_duration_ms_bucket[$__rate_interval])))` | User-perceived responsiveness (TTFB-like) | Set per workload; start from your current p95 baseline + 30% | Treating missing first-response samples as zero (they are "not emitted", not "fast") |
| Full request latency p95 | `histogram_quantile(0.95, sum by (le) (rate(aevatar_api_request_duration_ms_bucket[$__rate_interval])))` | End-to-end user waiting time | Set per workflow family; usually looser than first response | Assuming high full latency alone means platform issue |
| API request latency p99 | `histogram_quantile(0.99, sum by (le) (rate(aevatar_api_request_duration_ms_bucket[$__rate_interval])))` | Tail-latency regression detection | Alert when sustained spike exceeds SLO budget | Using p99 as primary product KPI instead of engineering diagnostic |
| Runtime event handle latency p95/p99 | `histogram_quantile(0.95, sum by (le) (rate(aevatar_runtime_event_handle_duration_ms_bucket[$__rate_interval])))` and p99 equivalent | Detects platform/runtime overhead changes independent of model generation variance | Trigger when both p95 and p99 trend up with stable traffic | Correlating directly to user latency without checking first/full API latency pair |

### 9.2 Diagnostic Order (Fast Triage)

1. Check API and runtime event error ratio (incident vs non-incident).
2. Check first-response p95 (user "is it responsive?" signal).
3. Check full-request p95 and API p99 (overall wait and tail behavior).
4. Check runtime event latency p95/p99 (platform overhead confirmation).
5. If only full-request worsens while first-response is stable, prioritize model/downstream generation analysis.

Error-ratio panel implementation note: dashboard queries use "empty-as-zero" (`or on() vector(0)`) to keep both API and runtime ratio series visible even when a 5-minute window has no error samples.

## 10. Next Plan Items

1. Add dashboard status coloring for core SLO panels.
2. Re-baseline alert thresholds after collecting production traffic.
3. Add tests:
   - WebSocket path first-response integration tests (requires WebSocket mock infrastructure)
4. Re-evaluate `IMeterFactory` only if host lifecycle isolation or meter injection becomes a proven need.

## 11. Default Histogram Buckets and Alerts

### 11.1 Default Histogram Buckets

Configured in `ObservabilityExtensions`:

- API latency histograms (`aevatar_api_request_duration_ms`, `aevatar_api_first_response_duration_ms`):
  - `25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 20000, 30000, 45000, 60000, 90000, 120000` ms
- Runtime event latency histogram (`aevatar_runtime_event_handle_duration_ms`):
  - `1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000` ms

Configuration overrides:

- `Observability:Metrics:ApiLatencyBucketsMs`
- `Observability:Metrics:RuntimeLatencyBucketsMs`

Both settings accept comma-separated millisecond boundaries in ascending order.

### 11.2 Default Alert Examples

The repository now includes Prometheus alert examples in `tools/observability/prometheus/alerts.yml`:

- API error ratio above 1% for 10 minutes
- Runtime event error ratio above 1% for 10 minutes
- API first-response p95 above 5000 ms for 10 minutes

These are default guardrails for local and pre-production validation. They should be tuned after collecting real workload baselines.

## 12. Related Documents

- `docs/architecture/stream-first-tracing-design.md`
- `docs/architecture/workflow-jaeger-observability-guide.md`
