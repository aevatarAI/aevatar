# Metrics Observability Dev-Diff Scorecard

> Branch: `feature/metrics-observability-plan` vs `dev`
> Date: 2026-03-05
> Scope: 21 files, +1239 / −136 lines

## Executive Summary

The metrics branch introduces a well-designed, low-cardinality observability baseline covering both the runtime event pipeline (`Aevatar.Agents`) and the API layer (`Aevatar.Api`). The first-response latency metric is a standout design choice for AI workloads. The primary refactoring concern is **metrics instrumentation leaking into business logic** rather than being composed orthogonally.

**Overall Score: 7.2 / 10**

---

## Dimension Scores

| # | Dimension | Score | Weight | Weighted |
|---|---|:---:|:---:|:---:|
| 1 | Architectural alignment (AGENTS.md) | 7 | 20% | 1.40 |
| 2 | Low cardinality & production safety | 9 | 15% | 1.35 |
| 3 | Separation of concerns | 6 | 20% | 1.20 |
| 4 | Test coverage & quality | 7 | 15% | 1.05 |
| 5 | Operational readiness | 7 | 10% | 0.70 |
| 6 | Code quality & maintainability | 6 | 10% | 0.60 |
| 7 | Documentation | 9 | 10% | 0.90 |
| | **Total** | | | **7.20** |

---

## Detailed Findings

### 1. Architectural Alignment (7/10)

**Good:**
- Two-Meter boundary is correct: `Aevatar.Agents` in `Foundation.Runtime`, `Aevatar.Api` in `Workflow.Infrastructure`.
- Host-layer wiring in `ObservabilityExtensions` — correct per layering rules.
- Runtime instrumentation in both `LocalActor` and `RuntimeActorGrain` — same metric contract.

**Issues:**
- **Static `Meter` instances.** Both `AgentMetrics` and `ApiMetrics` create `new Meter(...)` as static fields. .NET 8+ recommends `IMeterFactory` for DI-friendly meter creation. Static meters cannot be cleanly disposed and are harder to isolate in tests.
- **Coordinator return type changed for metrics concern.** `ChatWebSocketRunCoordinator.ExecuteAsync` changed from `Task` to `Task<double?>` to return first-response duration upward. This leaks the measurement concern into the coordinator's API contract. The coordinator shouldn't know about metrics timing.

### 2. Low Cardinality & Production Safety (9/10)

**Excellent:**
- Removed all per-entity labels (`agent.id`, `event.type`, `publisher.id`) — critical for production Prometheus.
- Removed low-value instruments (`RouteTargets`, `StateLoads`, `StateSaves`, `HandlerDuration`).
- Only 3 label keys total: `direction`, `result`, `transport`. Maximum label cardinality ≈ 2×2×2 = 8 series per instrument.
- `ResolveResult(statusCode)` treats 5xx as `error`, 4xx as `ok` — correct for SLO-oriented error ratio.

**Minor gap:**
- No explicit histogram bucket configuration. Default OTel buckets (5ms → 10s) are generic. For AI workloads where full-request can be 30-60+ seconds, custom buckets would improve percentile accuracy significantly.

### 3. Separation of Concerns (6/10)

**This is the main refactoring concern.**

Each endpoint method now carries 15-25 lines of metrics boilerplate:
- `Stopwatch.StartNew()` + `requestResult` variable
- Multiple `requestResult = ApiMetrics.ResolveResult(...)` / `ApiMetrics.ResultError` assignments
- `Interlocked.CompareExchange` for first-response
- `finally { RecordRequest(...) }`

This cross-cutting concern is **interleaved with business logic**. Example from `HandleChat`:

```59:149:src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs
// ~90 lines of business logic with metrics bookkeeping woven throughout
```

**Recommended refactoring direction:**
- Extract a lightweight `RequestMetricsScope` struct/class that encapsulates stopwatch, result tracking, and first-response recording.
- Endpoint methods call `scope.MarkResult(...)`, `scope.RecordFirstResponse()`, and `scope.Complete()` in `finally`.
- This reduces per-endpoint boilerplate to 3-4 lines and makes the pattern composable.

**Coordinator coupling:**

```7:10:src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs
// Return type changed from Task to Task<double?> solely for metrics
```

The coordinator should not return timing data. Instead, the caller can wrap the call:
```csharp
var sw = Stopwatch.StartNew();
await ChatWebSocketRunCoordinator.ExecuteAsync(...);
// first-response can be captured via callback closure, not return value
```

### 4. Test Coverage & Quality (7/10)

**Good:**
- `ApiMetricCapture` using `MeterListener` is the correct .NET approach for testing metric emissions.
- Tests cover: empty prompt (no first-response), streaming (first-response recorded), 404 classified as ok, 503 classified as error, exception classified as error.
- WebSocket path tests cover parse-fail first-response and execution-error metrics.

**Issues:**
- **Duplicated `ApiMetricCapture` class.** Identical copy-paste in `ChatEndpointsInternalTests.cs` and `WorkflowCapabilityEndpointsCoverageTests.cs` (including `MetricMeasurement` record). Should be extracted to a shared test utility.
- **No metrics on `HandleResume` / `HandleSignal`.** These endpoints have zero instrumentation. If they're part of the SLO surface, they're blind spots.
- **No test for cancellation-as-ok semantics.** The plan doc explicitly specifies `OperationCanceledException → result=ok`, but no test verifies this contract.

### 5. Operational Readiness (7/10)

**Good:**
- Docker Compose stack with Prometheus + Grafana provisions out of the box.
- Dashboard organized as SLO section (default view) + Runtime Diagnostics (drill-down).
- Error ratio panel uses `or on() vector(0)` for empty-window handling.
- Dashboard is `"editable": false` — correct for provisioned dashboards.

**Gaps:**
- No alert rule definitions (noted as pending in plan).
- No custom histogram bucket boundaries for AI-workload latency.
- `prometheus.yml` scrape target uses `host.docker.internal:5000` — works on macOS but not Linux Docker. Should document or provide alternative.

### 6. Code Quality & Maintainability (6/10)

**Good:**
- Helper methods (`RecordEventHandled`, `RecordRequest`, `RecordFirstResponse`) centralize tag construction.
- Const strings for all tag keys and values — no stringly-typed drift.
- `Interlocked.CompareExchange` for thread-safe once-only first-response recording.

**Issues:**
- **`requestResult` mutable tracking through long methods.** In `HandleCommand` (~120 lines), `requestResult` is mutated at 5 different points. Easy to miss a branch. A scope object would make this declarative.
- **Inconsistent cancellation handling.** WebSocket path: `OperationCanceledException` caught silently → `requestResult` stays `ok`. HTTP `HandleChat` path: no explicit `OperationCanceledException` catch → falls to generic `catch (Exception)` → classified as `error`. This violates the documented contract.
- **`RequestDurationMs` omits `result` tag.** `RequestsTotal` has `result`, but `RequestDurationMs` does not. This means you cannot compute per-result latency distributions. If intentional (histogram cardinality control), it should be documented as a deliberate choice.

### 7. Documentation (9/10)

**Excellent:**
- `metrics-baseline-plan.md` is comprehensive: purpose, principles, anti-patterns, status, contract, diagnostic model, runbook.
- "Why NOT split AI/core at runtime event level" explanation is well-reasoned and prevents future misguided refactoring.
- PromQL quick reference with alert thresholds and common misreads.
- First-response semantic contract is explicit (when recorded, when not).

**Minor:**
- The `RequestDurationMs` missing `result` tag is not documented as a deliberate design choice.

---

## Priority Refactoring Recommendations

| Priority | Issue | Effort | Impact |
|:---:|---|:---:|:---:|
| P1 | Extract `RequestMetricsScope` to decouple metrics boilerplate from endpoint logic | M | High |
| P1 | Fix cancellation inconsistency (HTTP path should also classify `OperationCanceledException` as `ok`) | S | Medium |
| P2 | Remove coordinator return-type coupling — use callback closure for first-response timing | S | Medium |
| P2 | Extract shared `ApiMetricCapture` test helper to avoid copy-paste | S | Low |
| P3 | Add `HandleResume` / `HandleSignal` instrumentation or document exclusion | S | Medium |
| P3 | Configure custom histogram bucket boundaries for AI-latency workloads | S | Medium |
| P4 | Migrate static `Meter` to `IMeterFactory` pattern (when DI support is ready) | M | Low |
| P4 | Add cancellation-as-ok test case | S | Low |

---

## Verdict

A solid observability baseline with strong design principles (low cardinality, two-meter boundary, first-response semantics) and comprehensive documentation. The main debt is **metrics instrumentation interleaved with business logic** — extracting a scope/wrapper pattern would raise the SoC score from 6 to 8+ and make the pattern sustainable as more endpoints are added. The cancellation inconsistency between HTTP and WebSocket paths should be fixed before merge to honor the documented contract.
