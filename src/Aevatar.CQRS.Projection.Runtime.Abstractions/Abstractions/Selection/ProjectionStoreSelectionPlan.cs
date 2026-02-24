namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public readonly record struct ProjectionStoreSelectionPlan(
    ProjectionStoreRequirements DocumentRequirements,
    ProjectionStoreSelectionOptions DocumentSelectionOptions,
    ProjectionStoreRequirements GraphRequirements,
    ProjectionStoreSelectionOptions GraphSelectionOptions);
