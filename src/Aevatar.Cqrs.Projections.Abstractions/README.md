# Aevatar.Cqrs.Projections.Abstractions

Contracts-only project for CQRS projection.

## Contains

- Projection service/orchestrator/projector/reducer/store interfaces
- Run-scoped projection context/session contracts
- Read-model contracts (`ChatRunReport` and related types)

## Rules

- No endpoint, DI, store implementation, or rendering concerns.
- Keep this project stable; implementation details belong to `Aevatar.Cqrs.Projections`.
