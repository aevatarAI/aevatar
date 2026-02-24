namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionStoreSelectionPlanner
{
    ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionReadModelRequirements relationRequirements);
}
