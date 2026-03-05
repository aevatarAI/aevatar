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
- Use Prometheus + Grafana as the default display stack.

## 3. Anti-Patterns to Avoid

- Per-actor/per-session/per-command labels in metrics.
- Parallel metric systems with overlapping names and meanings.
- Multiple `Meter` objects sharing the same name across assemblies.
- Dashboard-only metrics that are not used in SLO/SLA decisions.
- `TypeUrl.Contains(...)` in src (blocked by CI guard).

## 4. Current Implementation Status

Implemented:

- Prometheus scraping endpoint (`/metrics`) in `Workflow.Host.Api`.
- Grafana + Prometheus local stack (`docker-compose.observability.yml`).
- Runtime metric cleanup and low-cardinality refactor:
  - removed high-cardinality labels (`agent_id`, `publisher_id`).
  - removed low-value instruments (`RouteTargets`, `StateLoads`, `StateSaves`, `HandlerDuration`).
- Runtime metrics emitted from both Local and Orleans paths.
- API metrics for request count and full duration (meter `Aevatar.Api`) on all endpoints (chat, command, websocket).
- API first-response duration metric for streaming paths and WS parse error responses.
- Grafana dashboard panels for:
  - health/error ratio
  - runtime/API throughput and latency
  - window totals (`increase`)
  - first-response vs full-response comparison

Pending:

- Add explicit SLO panel with thresholds and status coloring.
- Add alert rule examples (Prometheus/Grafana alerting).
- Add WebSocket path first-response integration tests (requires WebSocket mock infrastructure).

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
  - recorded on first streamed output frame (`emitAsync`) with `result=ok`.
  - not recorded for prompt validation early return (400) because no response stream frame is produced.
- `ws` path:
  - recorded on first outbound message among `command ack`, `agui event`, or `command error`.
  - if the request fails before any websocket message is sent, no first-response metric is recorded.

This contract intentionally tracks "first observable response signal" rather than "request finished".

### 5.4 Cancellation Semantics

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
5. API Request Latency p95/p99 (timeseries)
6. Runtime Event Handle Latency p95/p99 (timeseries)

Panels (Runtime Diagnostics section, drill-down):

7. Runtime Diagnostics Guide (text)
8. Active Actors (stat)
9. Runtime Events — Self, Window Total (stat)
10. API Requests — Window Total (stat)
11. Runtime Self Events / API Request (stat)
12. Runtime Events Rate — Self, by result (timeseries)
13. API Requests Rate by result (timeseries)

Local stack:

- `docker-compose.observability.yml`
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000`

## 8. Verification Checklist

- `curl http://localhost:5000/metrics` shows:
  - `aevatar_runtime_events_handled_total`
  - `aevatar_api_requests_total`
  - `aevatar_api_first_response_duration_ms`
- No high-cardinality labels appear in metric series.
- No `pipeline` label in runtime metrics.
- Prometheus target `aevatar-workflow-host` is `UP`.
- Dashboard shows first-response vs full-response latency.

## 9. Next Plan Items

1. Add SLO panel group:
   - API error ratio (5m)
   - Runtime error ratio (5m)
   - API first-response p95
2. Add alert threshold defaults:
   - API error ratio > 1%
   - Runtime error ratio > 1%
   - First response p95 > threshold
3. Add tests:
   - WebSocket path first-response integration tests (requires WebSocket mock infrastructure)

## 10. Related Documents

- `docs/architecture/stream-first-tracing-design.md`
- `docs/architecture/workflow-jaeger-observability-guide.md`
