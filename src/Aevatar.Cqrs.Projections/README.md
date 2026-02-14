# Aevatar.Cqrs.Projections

## Directory Layout

- `Configuration/`: feature options (`ChatProjectionOptions`)
- `DependencyInjection/`: DI composition root (`AddChatProjectionCqrs`)
- `Orchestration/`: run-scoped projection coordination
- `Orchestration/ChatRunProjectionService`: app-facing projection facade (`IChatRunProjectionService`)
- `Projectors/`: read-model projector implementations
- `Reducers/`: event-specific reducers and mutation helpers
- `Stores/`: read-model store implementations

## Project Boundaries

- `Aevatar.Cqrs.Projections.Abstractions`
  - generic projection contracts (`IProjection*`)
  - chat aliases (`IChat*`)
  - run context/session contracts
  - read-model contracts (`ChatRunReport/*`)
- `Aevatar.Cqrs.Projections`
  - reducers/projectors/coordinator/store implementations
  - DI composition root (`AddChatProjectionCqrs`)

## Boundary Rules

- Keep endpoint/reporting concerns outside this project.
- Keep reducers focused on event folding only.
- Add new event projection by adding a reducer, avoid editing projector dispatch logic.
- Use `EventEnvelope.Id` for in-run dedup and envelope timestamp as projection time source.
- Run projection is event-driven: `ChatRunProjectionService` keeps one actor-level stream subscription and reuses it across active runs.
- Query remains separate from completion signal: wait with `WaitForRunProjectionCompletedAsync(runId)`, then read model via query API/service.
- Coordinator/projector/reducer/store runtime wiring should consume `IProjection*` contracts; `IChat*` is a domain alias layer.

## Extensibility (OCP)

- Built-in reducers/projectors are auto-discovered from this assembly.
- External modules can extend without changing this project:
  - `AddChatProjectionReducer<TReducer>()`
  - `AddChatProjectionProjector<TProjector>()`
  - `AddChatProjectionExtensionsFromAssembly(assembly)`
- Extension components are also exposed under generic contracts (`IProjectionEventReducer<,>`, `IProjectionProjector<,>`) for model-agnostic composition.
- Auto-discovery only registers public concrete types (for predictable plugin boundaries).
