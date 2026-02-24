namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionStoreSelectionPlanner
{
    ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionReadModelRequirements relationRequirements);
}
