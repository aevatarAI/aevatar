---
title: 0022 — OpenTelemetry semantic conventions for aevatar.* activities
status: proposed
owner: eanzhao
---

# 0022 — OpenTelemetry semantic conventions for aevatar.* activities

## Status

Proposed (will move to Accepted when the first wave of activities lands in
`AevatarActivitySource` and the conventions are documented in
[docs/canon/observability.md](../canon/observability.md)). Co-issued with
ADR [0023](0023-two-tier-inspector-architecture.md) which depends on these
conventions to drive the Inspector's observation tier.

## Context

`AevatarActivitySource` (`src/Aevatar.Foundation.Abstractions/Observability/AevatarActivitySource.cs`)
today emits a small set of activities — `HandleEvent:{eventTypeName}`,
`invoke_agent`, `chat`, `execute_tool` — with tags `aevatar.agent.id`,
`aevatar.event.id`, `aevatar.event.type`, `aevatar.event.direction`,
`aevatar.event.publisher`. The AI layer's `GenAIActivitySource`
(`src/Aevatar.AI.Core/Observability/GenAIActivitySource.cs`) covers LLM and
tool execution under the standard OpenTelemetry GenAI semantic conventions.

Several recurring observation needs are uncovered:

- **Actor lifecycle is invisible.** Spawn, deactivate, parent-child link /
  unlink — no traces. An external consumer (Jaeger / Tempo / Honeycomb) cannot
  reconstruct "what actors became alive when".
- **Projection materialization is invisible.** Every projector consumes
  committed events via `IProjectionMaterializer<TContext>.ProjectAsync` but
  emits no activity, so projection lag and per-projection throughput are not
  observable.
- **Readmodel writes are invisible.** `IProjectionWriteDispatcher<TReadModel>`
  `UpsertAsync` / `DeleteAsync` are the canonical write path into the
  document store; they emit nothing.
- **Workflow runs are partly observable but not in OTel.** The workflow
  runtime already streams `WorkflowEvent` over SSE, but those events do
  not land in OTel traces, so a workflow run does not appear in a Tempo /
  Jaeger view alongside the actor activities it consumes.

A planned local Inspector demo (`demos/Aevatar.Demos.Inspector`, see ADR
[0023](0023-two-tier-inspector-architecture.md)) needs these signals. We
considered two non-OTel routes for the Inspector — implementing a
dedicated `IGAgentExecutionHook` + custom channel ("hook-driven live bus")
or extending the workflow `WorkflowEvent` stream to actors. Both create a
second observation pipeline parallel to OTel, violating CLAUDE.md's
"禁止双轨实现" rule and providing zero value to production aevatar
deployments that already pipe `AevatarActivitySource` to Jaeger / Tempo /
Honeycomb.

The cleaner path is to extend `AevatarActivitySource` to cover the missing
ground, publish those conventions in [docs/canon/observability.md](../canon/observability.md),
and let any consumer — Inspector demo, production observability stacks,
future debugging tools — feed off the same canonical activity stream.

## Decision

Introduce eight new activities and four new tag keys under the
`aevatar.*` namespace, all emitted from a single `Aevatar.Agents`
ActivitySource (the existing `AevatarActivitySource` instance, extended in
place). All new conventions are marked **experimental** at introduction;
promotion to stable requires a follow-up ADR. The GenAI conventions
(`gen_ai.*` family) emitted by `GenAIActivitySource` are unchanged.

### Activities (new)

| Activity name | Kind | When fired | Required tags | Optional tags |
|---------------|------|------------|---------------|---------------|
| `aevatar.agent.spawn` | Internal | First-time activation of an actor (LocalActorRuntime.CreateAsync after the idempotent-return path); **must not** fire on idempotent return | `aevatar.agent.id`, `aevatar.agent.type` | — |
| `aevatar.agent.deactivate` | Internal | Actor is destroyed (DestroyAsync) or deactivated via `IActorDeactivationHook` | `aevatar.agent.id` | `aevatar.agent.type` |
| `aevatar.agent.link` | Internal | `IActorRuntime.LinkAsync(parentId, childId)` completes | `aevatar.agent.parent`, `aevatar.agent.id` (= childId) | — |
| `aevatar.agent.unlink` | Internal | `IActorRuntime.UnlinkAsync(childId)` completes | `aevatar.agent.parent`, `aevatar.agent.id` (= childId) | — |
| `aevatar.projection.materialize` | Internal | `IProjectionMaterializer<TContext>.ProjectAsync` enters; spans the wrapped call | `aevatar.projection.name`, `aevatar.projection.last_event_id` | `aevatar.projection.state.version` (set on success), `aevatar.workflow.run_id` / `aevatar.workflow.step` (when the context is a workflow projection context) |
| `aevatar.readmodel.upsert` | Internal | `IProjectionWriteDispatcher<TReadModel>.UpsertAsync` enters | `aevatar.readmodel.name`, `aevatar.readmodel.state.version` | — |
| `aevatar.readmodel.delete` | Internal | `IProjectionWriteDispatcher<TReadModel>.DeleteAsync` enters | `aevatar.readmodel.name`, `aevatar.readmodel.id` | — |
| `aevatar.workflow.run` | Internal | Decorates the entry of `WorkflowExecutionRunEventProjector` for a workflow run | `aevatar.workflow.run_id`, `aevatar.workflow.name` | `aevatar.workflow.step` |

### Tags (new, in addition to the existing `aevatar.agent.id` and `aevatar.event.*` set)

| Tag key | Type | Meaning | Notes |
|---------|------|---------|-------|
| `aevatar.agent.type` | string | Concrete `IAgent` type name (e.g. `ChatGAgent`) | Add to `HandleEvent:*` activities as well, so consumers can group by type without joining tables |
| `aevatar.agent.parent` | string | Parent actor id (the parent in the runtime topology at the time of the activity) | Appears on `aevatar.agent.link` and `aevatar.agent.unlink` only. **Does not appear on spawn**, because parent-child relationships are dynamic and assigned via `LinkAsync` after activation. |
| `aevatar.projection.name` | string | Materialization context type name (e.g. `GAgentRegistryCurrentStateProjectionContext`) | One value per projector |
| `aevatar.projection.state.version` | int64 | State version after a successful materialize | Set as post-call tag on `aevatar.projection.materialize`. Absent on failure paths. |
| `aevatar.projection.last_event_id` | string | The committed event id being materialized | From `EventEnvelope.EventId` |
| `aevatar.readmodel.name` | string | Concrete `IProjectionReadModel` type name (e.g. `GAgentRegistryCurrentStateDocument`) | One value per readmodel |
| `aevatar.readmodel.state.version` | int64 | State version being written | Required on `aevatar.readmodel.upsert`; absent on delete |
| `aevatar.readmodel.id` | string | Readmodel id being deleted | Required on `aevatar.readmodel.delete`; absent on upsert |
| `aevatar.workflow.run_id` | string | Workflow run id (from `WorkflowExecutionCurrentStateDocument` schema) | Appears on workflow-related activities and on `aevatar.projection.materialize` when context is a workflow projection |
| `aevatar.workflow.name` | string | Workflow name | Appears on `aevatar.workflow.run` |
| `aevatar.workflow.step` | string | Step name within the workflow | Appears on `aevatar.workflow.run` and `aevatar.workflow.step`-tagged materialize activities |

### ActivitySource

All new activities share the existing `Aevatar.Agents` source. We do not
split the source per layer (Foundation / Projection / Workflow). The
single-source choice keeps consumer-side filtering trivial
(`ShouldListenTo: name == "Aevatar.Agents"`) and matches the existing
`HandleEvent:*` pattern. LLM / Tool activities continue on
`Aevatar.GenAI` (`GenAIActivitySource`) per the OTel GenAI SemConv
standard — that source is independent and out of scope for this ADR.

### Stability commitment

All new tag keys and activity names ship as **experimental** at
introduction. The semantic conventions doc [docs/canon/observability.md](../canon/observability.md)
classifies each entry as `[experimental]` or `[stable]`. An experimental
key may be renamed, removed, or have its value semantics changed in any
release; a stable key follows a deprecation cycle (announce, parallel
emit, remove in the next major). Promotion `experimental → stable`
requires a follow-up ADR that names each key being promoted and confirms
in-the-field telemetry usage.

### What this is not

This ADR does **not**:

- Introduce a new observation bus, channel, or event stream parallel to OTel.
- Change `WorkflowEvent` emission. The existing `WorkflowEvent` SSE stream
  serving Workflow Studio remains untouched; `aevatar.workflow.run` /
  `aevatar.workflow.step` activities decorate the server-side projector
  path (`WorkflowExecutionRunEventProjector`), they do not fan out a
  second emit. Both Workflow Studio and the Inspector demo consume
  observations downstream of the same fact source.
- Change projection registration ergonomics for callers. The decorator
  for materialize / write activities is wired centrally inside
  `ProjectionMaterializerRegistration` and `AddProjectionReadModelRuntime`,
  so existing projector registrations get the new activities "for free"
  with no per-projector code change.
- Change sampling defaults. Production samplers continue to govern the
  rate; the Inspector demo overrides to `AlwaysOn` locally (see ADR
  [0023](0023-two-tier-inspector-architecture.md)).

## Alternatives considered

**Per-layer ActivitySources** (`Aevatar.Projection`, `Aevatar.Workflow`,
`Aevatar.Agents`, …). Rejected. Each consumer must enumerate sources
which is fragile, and the existing single-source pattern is already
consumed externally; splitting now creates a breaking change with no
clear benefit.

**Dedicated observation bus** (a `Channel<InspectorEvent>` populated by
custom `IGAgentExecutionHook` + `IProjectionMaterializer` decorators,
consumed by Inspector SSE). Rejected. This is two parallel observation
pipelines in the same process (OTel + custom bus). Violates
"禁止双轨实现". Production observability stacks see only the OTel half.

**Subscribe to committed events directly** (consume the same event store
that projectors consume). Rejected. The Inspector is an observation
consumer, not a projector. Re-running projection-like logic in Inspector
duplicates work and risks divergence. The conventional OTel surface is
both lighter and standard.

**Use OTel Logs / Metrics instead of Activities for some signals.**
Considered for projection materialize counts and readmodel write rates.
Defer: the V1 Inspector renders animation per event, which is naturally
span-shaped. Metrics (counters / histograms) can layer in later under a
separate `Aevatar.Agents` Meter and a follow-up ADR.

## Consequences

- **Production stacks gain ground without owning Inspector.** Any aevatar
  deployment that already exports `AevatarActivitySource` to an OTel
  collector gets the new lifecycle / projection / workflow spans for
  free. Existing Tempo / Jaeger / Honeycomb dashboards become richer
  without code changes on the consumer side.
- **The Inspector demo is a thin downstream consumer.** ADR
  [0023](0023-two-tier-inspector-architecture.md) describes the
  in-process `ActivityListener` that consumes these activities for
  animation. The Inspector contributes zero new instrumentation; it just
  visualizes what `Aevatar.Agents` already emits.
- **Activity emit must be infallible.** A failing tag set or
  ActivityListener handler must never propagate into the business path
  of the wrapped operation. The decorator implementation wraps tag
  operations in `try { ... } catch { /* swallow */ }` blocks (CLAUDE.md
  "正确架构优先：选择正确的架构设计").
- **Tag rename has cost once published.** Even experimental tags have an
  observable footprint after release; future consumers may pin
  dashboards on names. Promotion to stable requires explicit ADR review.
- **CI guards.** A new architecture guard (introduced under ADR
  [0023](0023-two-tier-inspector-architecture.md)) ensures the Inspector
  consumer never treats OTel activities as canonical state. Other
  existing guards (`projection_state_version_guard.sh`, etc.) are
  unaffected by the decorator wiring, which only adds activities and
  does not change projection semantics.

## References

- ADR [0023](0023-two-tier-inspector-architecture.md) — Two-tier
  Inspector architecture (canonical readmodel vs observation OTel).
- [docs/canon/observability.md](../canon/observability.md) — Living
  semantic conventions for `aevatar.*` activities; this is the
  source-of-truth document and the place a consumer goes to learn what
  tag exists right now.
- [OpenTelemetry GenAI Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/) —
  reference for the `gen_ai.*` family emitted by `GenAIActivitySource`,
  which this ADR does not modify.
- [docs/canon/architecture.md](../canon/architecture.md) — repository
  architecture overview that names the layers (Foundation / Application
  / Infrastructure / Host) this ADR's activities span.
