# Metrics Observability Dev-Diff Scorecard

> Branch: `feature/metrics-observability-plan` vs `dev`
> Date: 2026-03-06
> Scope: observability baseline, alert examples, and contract documentation refresh

## Executive Summary

The branch now delivers a strong, layered observability baseline for both runtime (`Aevatar.Agents`) and API (`Aevatar.Api`) paths. The most important earlier concerns have been addressed:

- instrumentation is centralized behind `ApiRequestScope` and `EventHandleScope`
- `HandleResume` / `HandleSignal` are included in API request metrics
- first-response semantics are aligned with the first observable response signal
- Prometheus alert examples and explicit histogram buckets are now part of the baseline

The remaining architectural follow-up is whether meter lifecycle needs to be DI-managed in the future. That is now a secondary concern rather than a blocker for this baseline.

**Overall Score: 9.0 / 10**

---

## Dimension Scores

| # | Dimension | Score | Weight | Weighted |
|---|---|:---:|:---:|:---:|
| 1 | Architectural alignment (AGENTS.md) | 9 | 20% | 1.80 |
| 2 | Low cardinality & production safety | 9 | 15% | 1.35 |
| 3 | Separation of concerns | 9 | 20% | 1.80 |
| 4 | Test coverage & quality | 8 | 15% | 1.20 |
| 5 | Operational readiness | 9 | 10% | 0.90 |
| 6 | Code quality & maintainability | 8 | 10% | 0.80 |
| 7 | Documentation | 9 | 10% | 0.90 |
| | **Total** | | | **8.75** |

Rounded review verdict: **9.0 / 10**

---

## Detailed Findings

### 1. Architectural Alignment (9/10)

**Strong points:**
- Two-meter boundary remains clean: `Aevatar.Agents` in runtime, `Aevatar.Api` in workflow capability API.
- Host-layer OpenTelemetry wiring stays in `ObservabilityExtensions`, preserving layering.
- Runtime observability continues to flow through `EventHandleScope`; API observability continues to flow through `ApiRequestScope`.

**Remaining follow-up:**
- Metric emission still uses shared static meters internally. This is acceptable for the current scale; `IMeterFactory` is a future refinement only if lifecycle ownership or host-level isolation becomes materially important.

### 2. Low Cardinality & Production Safety (9/10)

**Excellent:**
- No high-cardinality identifiers are used as metric labels.
- Runtime labels remain bounded to `direction` and `result`; API labels remain bounded to `transport` and `result`.
- 5xx status codes are classified as `error`, while 4xx and cancellations stay out of the service-error ratio.
- `aevatar_api_request_duration_ms` intentionally omits `result`, which keeps histogram cardinality controlled.

**Minor follow-up:**
- Bucket defaults are now explicit and reasonable for AI latency profiles, but should still be tuned after observing production traffic.

### 3. Separation of Concerns (9/10)

**Resolved from the earlier review:**
- Metrics are no longer woven into endpoint methods via ad hoc stopwatch/result bookkeeping.
- `ApiRequestScope` owns timing, result classification, and first-response tracking.
- `EventHandleScope` owns runtime handle timing, tracing, log scope, and result recording.
- `ChatWebSocketRunCoordinator` stays metrics-aware only through the passed scope, not through custom timing return types.

This is the correct direction for a branch whose primary goal is a maintainable observability baseline rather than feature logic changes.

### 4. Test Coverage & Quality (8/10)

**Good:**
- Shared `ApiMetricCapture` helper is in place and used by API tests.
- Tests cover first-response, request result classification, cancellation-as-ok semantics, and newly instrumented `resume` / `signal` endpoints.
- Existing WebSocket tests still validate parse-fail, start-error, and execution-error metric behavior.

**Remaining gap:**
- Runtime-side metrics behavior is covered less directly than the API side; most confidence there still comes from scope behavior and integration shape rather than more focused runtime metrics tests.

### 5. Operational Readiness (9/10)

**Improved significantly:**
- Prometheus and Grafana local stack remains straightforward to run.
- Prometheus alert examples are now checked in under `tools/observability/prometheus/alerts.yml`.
- Histogram buckets are explicitly configured for AI-latency workloads.
- Documentation now calls out Docker-host assumptions and Linux-specific host-target adjustment.

**Remaining gap:**
- Alert thresholds are defaults, not production-calibrated values.

### 6. Code Quality & Maintainability (8/10)

**Good:**
- Scope-based instrumentation keeps business call sites compact while preserving stable metric names and exported series.
- Existing metric names and exported series remain stable, preserving dashboards and tests.
- Scope APIs remain simple for business callers.

**Remaining gap:**
- Static meter ownership remains in helper classes. That is acceptable for the current branch, but still less flexible than a full DI-managed meter lifecycle if the codebase outgrows this shape.

### 7. Documentation (9/10)

**Strong:**
- `metrics-baseline-plan.md` now matches the code more closely, including:
  - first-response semantics on the HTTP bootstrap frame
  - `resume` / `signal` coverage
  - explicit bucket defaults
  - alert file locations
  - the deliberate omission of `result` from request-duration histograms
- `tools/observability/README.md` now covers alert rules, bucket overrides, and Linux host-target notes.

---

## Priority Follow-Ups

| Priority | Issue | Effort | Impact |
|:---:|---|:---:|:---:|
| P1 | Add a few targeted tests around runtime metrics behavior and activation/deactivation counters | S | Medium |
| P2 | Tune alert thresholds and histogram boundaries with real traffic baselines | S | Medium |
| P3 | Revisit `IMeterFactory` only if meter lifecycle isolation becomes a demonstrated need | M | Low |
| P3 | Add dashboard status coloring / richer SLO state presentation | S | Low |

---

## Verdict

This diff is now at a strong review standard. The branch demonstrates clear layered observability design, low-cardinality discipline, realistic AI-latency thinking, and materially improved operational readiness. The remaining `IMeterFactory` discussion is intentionally deferred so the code stays simpler while the observability baseline remains strong.
