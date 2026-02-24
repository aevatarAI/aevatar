namespace Aevatar.CQRS.Projection.Abstractions;

public readonly record struct ProjectionStoreSelectionPlan(
    ProjectionReadModelRequirements ReadModelRequirements,
    ProjectionReadModelStoreSelectionOptions ReadModelSelectionOptions,
    ProjectionReadModelRequirements RelationRequirements,
    ProjectionReadModelStoreSelectionOptions RelationSelectionOptions);
