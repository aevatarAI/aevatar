---
title: 0023 — Two-tier Inspector architecture (canonical readmodel vs observation OTel)
status: proposed
owner: eanzhao
---

# 0023 — Two-tier Inspector architecture (canonical readmodel vs observation OTel)

## Status

Proposed (will move to Accepted when the Inspector demo lands in
`demos/Aevatar.Demos.Inspector` and the boundary guard
`tools/ci/inspector_tier_boundary_guard.sh` is wired into CI). Depends on
ADR [0022](0022-otel-aevatar-semantic-conventions.md) for the OTel
activities the observation tier consumes.

## Context

`demos/Aevatar.Demos.Inspector` is a local developer tool that
visualizes a running aevatar process: actors come alive, messages travel
between them, projections materialize, workflows progress, readmodels
update. The user needs both:

- **Ground truth** — "Which actors are alive right now? What is the
  current state of readmodel X? What workflow runs are in progress?"
  Answering these correctly is the whole reason the tool is trusted.
- **Live animation** — "An event just hit actor A. A projection just
  materialized version 42. A workflow just advanced to step 3." These
  signals are how the tool feels alive.

These needs pull in opposite directions. Ground truth requires a
canonical source, complete coverage, exact semantics — i.e. a readmodel
or projection backed by the committed event store. Live animation
requires a low-latency, high-fidelity event stream — i.e. an OTel
ActivitySource that is sampled and may drop.

The naive implementation conflates them: the Inspector subscribes to OTel
activities, builds an in-process map of "actor id → current state from
the last observed lifecycle activities", and answers `GET /api/inspector/actors`
from that map. This violates three CLAUDE.md rules at once:

- **"事实源唯一"** — the runtime + projection doc store is the only truth;
  observation traces are not truth.
- **"查询走 readmodel"** — queries cannot be served by reconstruction over
  observation logs.
- **"禁止中间层维护 entity/actor/workflow-run/session 等 ID → 上下文/事实
  状态的进程内映射"** — a Dictionary keyed by actor id and holding live
  state is exactly the pattern this rule forbids.

It also fails operationally. OTel listeners are sampled, lossy, and
order-best-effort. A dropped `aevatar.agent.deactivate` span leaves a
"ghost actor" in the Inspector's map. A reordered `link` / `unlink` flips
the topology. These artifacts are silent — the user has no way to know
the Inspector is lying.

The cleaner architecture, which this ADR commits to, is to **explicitly
separate the two tiers and never let them cross**.

## Decision

The Inspector backend (`demos/Aevatar.Demos.Inspector`) is structured in
two strictly separated tiers. No code path serves a query from
observation events. No code path treats a readmodel as a transient event
stream.

### Tier 1 — Canonical state (the only source of truth)

Every `GET /api/inspector/*` endpoint that answers "what exists" or
"what state" reads from a readmodel / projection document store via the
existing query port:

| Endpoint | Backing readmodel | Backing query port |
|----------|-------------------|---------------------|
| `GET /api/inspector/actors` | `GAgentRegistryCurrentStateDocument` | the existing registry read port (e.g. `IGAgentRegistryCurrentStateQueryPort` or whichever public read port the Studio Projection module exposes) |
| `GET /api/inspector/workflow-runs` | `WorkflowExecutionCurrentStateDocument` | `IWorkflowExecutionCurrentStateQueryPort` (`src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionCurrentStateQueryPort.cs`) |
| `GET /api/inspector/readmodels` | enumerate `IProjectionDocumentMetadataProvider` impls | metadata providers |
| `GET /api/inspector/readmodels/:name` | the named readmodel | its existing read port |

Properties of Tier 1:

- Backed by the canonical event store (via the projector that produced
  the readmodel). Eventually consistent, but the truth.
- Idempotent. The same query returns the same answer for the same state
  version.
- Lossless. Tier 1 cannot lose actors or workflow runs because of
  sampling; it reads documents.
- Serializes Protobuf `state_root` (`google.protobuf.Any`) into typed
  JSON at the Host → browser boundary using
  `Google.Protobuf.JsonFormatter` (or the existing Studio Projection
  helper, whichever the implementation discovers in Phase A.3).

### Tier 2 — Observation (animation only)

A single endpoint, `GET /api/inspector/events`, streams OTel activities
via SSE:

```
ActivitySource "Aevatar.Agents"
  → in-process ActivityListener (ShouldListenTo == "Aevatar.Agents")
  → BoundedChannel<TelemetryFrame>(capacity: 1000, FullMode: DropOldest)
  → SSE response writer
  → browser
```

Properties of Tier 2:

- Sampled, lossy, drop-oldest under load.
- **No ring buffer**. The channel is a live broadcaster only. A SSE
  consumer that connects late sees only future frames; it does not
  replay history.
- The channel's only consumers are SSE responses. **No HTTP endpoint
  reads from the channel for a query result.** Architecture guard
  `tools/ci/inspector_tier_boundary_guard.sh` enforces this.
- Used for UI animation: actor pulses, message particles, projection
  "river" droplets, workflow ribbon ticks.

### The boundary rule

A single rule defines the architecture:

> **Tier 2 is decoration. Tier 1 is truth. A Tier 1 endpoint must never
> read Tier 2 state. A Tier 2 stream must never serve as a query result.**

Concrete consequences:

- The `BoundedChannel<TelemetryFrame>` has no historical depth and
  exposes no read-by-id surface.
- `/api/inspector/events` is the only consumer of the channel, and it is
  a stream (no query semantics).
- Restarting the Inspector loses Tier 2 history (acceptable, by design).
  Tier 1 is unaffected because it reads from the doc store.
- Disabling the `ActivityListener` stops UI animation; Tier 1 endpoints
  continue working. A failing OTel pipeline degrades the tool's feel,
  not its correctness.
- Front-end UX: on load, the page fetches Tier 1 to render the static
  topology, then attaches to Tier 2 SSE for animation. Periodic re-poll
  of Tier 1 (5s default) keeps the canonical state fresh. A future watch
  endpoint can replace polling without altering the tier rule.

### CI guard

`tools/ci/inspector_tier_boundary_guard.sh` scans
`demos/Aevatar.Demos.Inspector*` for forbidden patterns:

- An endpoint method (`/api/inspector/*`) that reads
  `Channel<TelemetryFrame>` or any telemetry buffer.
- An endpoint method that returns a list / history of telemetry frames.
- A query handler that reconstructs entity state from OTel activity
  records.

Without the guard the boundary is documentation only — a PR can quietly
drain the buffer into a `/api/inspector/recent-events` endpoint and the
architectural invariant disappears. CLAUDE.md "治理前置：架构规则必须可
自动化验证" applies.

## Alternatives considered

**OTel as single source.** Reject. OTel is sampled and best-effort by
design; using it for canonical state silently introduces ghost actors
under load. The Inspector becomes untrustworthy at the moment debug help
is most needed (high traffic).

**Readmodel as single source, no Tier 2.** Reject. Without an event
stream the Inspector cannot show "what just happened" — no message
particles, no projection drops. The tool loses its identity as a live
debugger and becomes a slow polling dashboard.

**A custom observation bus (channel) populated by hooks, separate from
OTel.** Reject. Two parallel observation pipelines in the same process.
Production aevatar deployments see only the OTel half; the Inspector
sees only the custom half; nothing is exchanged. Violates
"禁止双轨实现". This is the architecture ADR
[0022](0022-otel-aevatar-semantic-conventions.md) explicitly avoids.

**Tier 2 with a small replay buffer for "warm start".** Considered for
UX: a newly-connecting SSE client could see the last 500 frames so
animation feels populated immediately. Reject for V1. A replay buffer is
a queue-of-records keyed implicitly by arrival time — a future
maintainer can wire a `/api/inspector/recent-events` endpoint into it
and the boundary rule erodes. V1 ships without the buffer; the cost is
that a freshly-connected client sees a static topology until the next
activity arrives, which is acceptable for a developer tool. A later
revision can introduce a bounded replay strictly inside the SSE
handler's connect path (never exposed as a query surface) if user
testing requires it.

## Consequences

- **Architecturally correct.** Both CLAUDE.md "事实源唯一" and "禁止中间层
  维护 ID → 上下文/事实状态的进程内映射" are upheld. Reviewers can verify
  the rule in five lines of bash (the CI guard).
- **Tier 1 latency is acceptable.** Readmodel queries hit the existing
  document store; lookups are O(1) for known ids, O(N) for enumerations
  where N is bounded by the registered actor count. 5s polling from the
  front-end is the default; a future watch endpoint can reduce
  perceived latency without touching the tier rule.
- **Tier 2 is intentionally lossy.** Under high event rates the
  `BoundedChannel<TelemetryFrame>(1000, DropOldest)` discards oldest
  frames. The UI shows fewer animation pulses, not wrong topology.
  Performance verification belongs in Phase A.5 (HandleEvent throughput
  with the listener attached must stay within 5% of baseline).
- **Wire-format JSON exception.** Tier 1 and Tier 2 both serialize JSON
  at the Host → browser boundary. CLAUDE.md's "Protobuf 优先" applies to
  the internal `actor↔actor` and `Host↔Host` wire formats; demo
  presentation to a browser is a documented exception, captured in
  [docs/canon/observability.md](../canon/observability.md).
- **Restart semantics.** Inspector restarts lose Tier 2 history; Tier 1
  endpoints rehydrate from the doc store. This is the intended
  behavior; if a user wants to "see what happened five minutes ago" the
  answer is to wire an OTel export to Tempo / Jaeger, which is exactly
  what ADR [0022](0022-otel-aevatar-semantic-conventions.md) enables for
  free.
- **Local-runtime scope.** The Inspector V1 embeds the Local actor
  runtime (`LocalActorRuntime`). Orleans-runtime observation is not
  covered; an Orleans grain activated lazily on first message will emit
  `aevatar.agent.spawn` on `OnActivateAsync` rather than `CreateAsync`,
  which is documented in [docs/canon/observability.md](../canon/observability.md)
  but out of scope for V1.

## References

- ADR [0022](0022-otel-aevatar-semantic-conventions.md) — the OTel
  semantic conventions the observation tier consumes.
- [docs/canon/observability.md](../canon/observability.md) — operational
  doc enumerating activities, tags, sampling defaults, and the JSON
  wire-format exception at the Host → browser boundary.
- `IGAgentRegistryCurrentStateDocument`,
  `WorkflowExecutionCurrentStateDocument` — the readmodels the Tier 1
  endpoints currently read.
- `BoundedChannel<T>` policy:
  `System.Threading.Channels.Channel.CreateBounded<TelemetryFrame>(new
  BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest,
  SingleReader = false, SingleWriter = false })`.
- CLAUDE.md sections "权威状态 / ReadModel / Projection", "中间层状态约束",
  "Command / Envelope / Dispatch" — the rules this ADR is calibrated against.
