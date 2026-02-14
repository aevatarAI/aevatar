# Aevatar.Cqrs.Projections.Abstractions

Contracts-only project for CQRS projection.

## Contains

- Generic projection contracts:
  - `IProjectionCoordinator<TContext, TTopology>`
  - `IProjectionProjector<TContext, TTopology>`
  - `IProjectionEventReducer<TReadModel, TContext>`
  - `IProjectionReadModelStore<TReadModel, TKey>`
- Chat-specific aliases over generic contracts (`IChat*`)
- Run-scoped projection context/session contracts
- Read-model contracts (`ChatRunReport` and related types)

## Rules

- No endpoint, DI, store implementation, or rendering concerns.
- Keep generic contracts stable; implementation details belong to `Aevatar.Cqrs.Projections`.
