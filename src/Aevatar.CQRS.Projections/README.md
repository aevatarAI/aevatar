# Aevatar.CQRS.Projections

## Directory Layout

- `Configuration/`: feature options (`WorkflowExecutionProjectionOptions`)
- `DependencyInjection/`: DI composition root (`AddWorkflowExecutionProjectionCQRS`)
- `Orchestration/`: run-scoped projection coordination
- `Orchestration/WorkflowExecutionProjectionService`: app-facing projection facade (`IWorkflowExecutionProjectionService`)
- `Projectors/`: read-model projector implementations
- `Reducers/`: event-specific reducers and mutation helpers
- `Stores/`: read-model store implementations

## Project Boundaries

- `Aevatar.CQRS.Projections.Abstractions`
  - generic projection contracts (`IProjection*`)
  - workflow-execution aliases (`IWorkflowExecution*`)
  - run context/session contracts
  - read-model contracts (`WorkflowExecutionReport/*`)
- `Aevatar.CQRS.Projections`
  - reducers/projectors/coordinator/store implementations
  - DI composition root (`AddWorkflowExecutionProjectionCQRS`)

## Boundary Rules

- Keep endpoint/reporting concerns outside this project.
- Keep reducers focused on event folding only.
- Add new event projection by adding a reducer, avoid editing projector dispatch logic.
- Use `EventEnvelope.Id` for in-run dedup and envelope timestamp as projection time source.
- Run projection is event-driven: `WorkflowExecutionProjectionService` keeps one actor-level stream subscription and reuses it across active runs.
- Query remains separate from completion signal: wait with `WaitForRunProjectionCompletedAsync(runId)`, then read model via query API/service.
- Coordinator/projector/reducer/store runtime wiring should consume `IProjection*` contracts; `IWorkflowExecution*` is a domain alias layer.

## Extensibility (OCP)

- Built-in reducers/projectors are auto-discovered from this assembly.
- External modules can extend without changing this project:
  - `AddWorkflowExecutionProjectionReducer<TReducer>()`
  - `AddWorkflowExecutionProjectionProjector<TProjector>()`
  - `AddWorkflowExecutionProjectionExtensionsFromAssembly(assembly)`
- Extension components are also exposed under generic contracts (`IProjectionEventReducer<,>`, `IProjectionProjector<,>`) for model-agnostic composition.
- Auto-discovery only registers public concrete types (for predictable plugin boundaries).
