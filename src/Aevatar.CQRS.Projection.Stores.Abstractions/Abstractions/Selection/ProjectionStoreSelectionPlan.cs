namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public readonly record struct ProjectionStoreSelectionPlan(
    ProjectionReadModelRequirements ReadModelRequirements,
    ProjectionReadModelStoreSelectionOptions ReadModelSelectionOptions,
    ProjectionReadModelRequirements RelationRequirements,
    ProjectionReadModelStoreSelectionOptions RelationSelectionOptions);
