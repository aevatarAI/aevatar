# CQRS Projection Architecture

## Goal

Build a maintainable read-side projection pipeline for chat workflow runs with clear boundaries:

- Event ingress: `EventEnvelope`
- Contracts + read model: `src/Aevatar.Cqrs.Projections.Abstractions/*`
- Projection implementation core: `src/Aevatar.Cqrs.Projections/*`
- Output adapters: API query endpoints and report writer

## Dependency Rules

Follow strict dependency direction:

1. `Endpoints` depends on `Aevatar.Cqrs.Projections.Abstractions` and `Aevatar.Cqrs.Projections.DependencyInjection`.
2. `Reporting` depends on `Aevatar.Cqrs.Projections.Abstractions.ReadModels`, not the other way around.
3. `Aevatar.Cqrs.Projections` depends on `Aevatar.Cqrs.Projections.Abstractions`, never on endpoint or report rendering details.
4. Reducers depend on event contracts and read model contracts only.

## Current Design

### Directory Layout

- `src/Aevatar.Cqrs.Projections.Abstractions/Abstractions/`
- `src/Aevatar.Cqrs.Projections.Abstractions/Orchestration/`
- `src/Aevatar.Cqrs.Projections.Abstractions/ReadModels/`
- `src/Aevatar.Cqrs.Projections/Configuration/`
- `src/Aevatar.Cqrs.Projections/DependencyInjection/`
- `src/Aevatar.Cqrs.Projections/Orchestration/`
- `src/Aevatar.Cqrs.Projections/Projectors/`
- `src/Aevatar.Cqrs.Projections/Reducers/`
- `src/Aevatar.Cqrs.Projections/Stores/`

### Core Pipeline

- `IChatRunProjectionService`: application-facing facade used by hosts/endpoints.
- `IChatProjectionCoordinator`: run-scoped projection orchestrator.
- `IChatRunProjector`: projector contract.
- `ChatRunProjectionService`: creates run context and manages actor-level stream subscription for projection (shared across active runs on the same actor).
- `WaitForRunProjectionCompletedAsync(runId)`: completion signal for one run projection; use this signal before querying read model.
- `ChatRunReadModelProjector`: routes event by protobuf `TypeUrl`, deduplicates by `EventEnvelope.Id`, and executes reducers.
- `IChatRunEventReducer`: single-event mutation unit.
- `InMemoryChatRunReadModelStore`: run read model store with clone-on-read.

Endpoint no longer pushes envelopes into projection pipeline directly; projection runs from stream subscription in CQRS service.
Projection completion signal does not carry query payload. Query is a separate read-model call.
WebSocket async path follows this sequence: `chat.command -> wait projection completion signal -> query read model -> push query.result`.

### Read Model

Read model types are now isolated under:

- `src/Aevatar.Cqrs.Projections.Abstractions/ReadModels/ChatRunReadModel.cs`

This removes previous coupling where read model contracts lived inside reporting code.

### Reducer Strategy

Current reducers:

- `StartWorkflowEventReducer`
- `StepRequestEventReducer`
- `StepCompletedEventReducer`
- `TextMessageEndEventReducer`
- `WorkflowCompletedEventReducer`

Adding a new event projection no longer requires editing core projector code:

- same assembly: add reducer class, auto-discovery picks it up
- external assembly: register via `AddChatProjectionReducer<T>()` or `AddChatProjectionExtensionsFromAssembly(assembly)`
- extension auto-discovery scope: public concrete reducer/projector types only

## Projection as Optional Feature

`ChatProjectionOptions` controls feature toggles:

- `Enabled`
- `EnableRunQueryEndpoints`
- `EnableRunReportArtifacts`

When disabled:

- `/api/chat` still works (SSE + AG-UI projection only)
- CQRS read-model projection is skipped
- `/api/runs` endpoints are not mapped

Example:

```json
{
  "ChatProjection": {
    "Enabled": false,
    "EnableRunQueryEndpoints": false,
    "EnableRunReportArtifacts": false
  }
}
```

Configuration source is bound once at startup and reused through DI (`ChatProjectionOptions` singleton), avoiding duplicated runtime config paths.
Core CQRS services remain registered; `Enabled` controls runtime projection behavior and endpoint exposure.

## Why Not AutoMapper for Event Projection

`EventEnvelope -> ReadModel` here is not 1:1 DTO mapping. It includes:

- multi-event aggregation
- ordering-sensitive state transitions
- derived fields and summaries
- idempotency requirements

So reducer-based domain logic is used for projection.  
AutoMapper is suitable only for read-model-to-DTO/view mapping at API boundary.

## Best-Practice Gaps (Remaining)

### P0 (Should do next)

1. Endpoint orchestration is still too large (`ChatEndpoints.HandleChat` mixes request handling, projection lifecycle, stream writing, and artifact persistence).
2. Read-model persistence is memory-only (no durable read store implementation).

### P1 (Recommended)

1. Move projection execution into an application service (`IChatRunExecutionService`) and keep endpoint thin.
2. Add reducer-level unit tests per event type and failure-path tests (out-of-order and duplicate events).
3. Promote idempotency from in-run dedup to durable dedup (e.g., checkpoint by `runId + eventId` in persistent store).

### P2 (Optional)

1. Split report HTML rendering into template-based renderer for maintainability.
2. Add observability metrics for projection lag, reducer failures, and store mutation latency.

## EventEnvelope Positioning

`EventEnvelope` is the transport envelope for all cross-actor events. CQRS projection consumes these envelopes and derives query models.  
This means the framework is event-driven first; read models are derived state.

For audio/video, real-time semantics are supported only if upstream events model stream chunks/time slices.  
The projection layer processes discrete events, not raw binary stream sockets directly.
