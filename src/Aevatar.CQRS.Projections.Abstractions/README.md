# Aevatar.CQRS.Projections.Abstractions

Contracts-only project for CQRS projection.

## Contains

- Generic projection contracts:
  - `IProjectionCoordinator<TContext, TTopology>`
  - `IProjectionProjector<TContext, TTopology>`
  - `IProjectionEventReducer<TReadModel, TContext>`
  - `IProjectionReadModelStore<TReadModel, TKey>`
- Workflow-execution aliases over generic contracts (`IWorkflowExecution*`)
- Run-scoped projection context/session contracts
- Read-model contracts (`WorkflowExecutionReport` and related types)

## Rules

- No endpoint, DI, store implementation, or rendering concerns.
- Keep generic contracts stable; implementation details belong to `Aevatar.CQRS.Projections`.
