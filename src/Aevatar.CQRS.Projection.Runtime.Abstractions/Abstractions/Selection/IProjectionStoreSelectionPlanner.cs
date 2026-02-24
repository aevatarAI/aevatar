namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreSelectionPlanner
{
    ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionStoreRequirements graphRequirements);
}
