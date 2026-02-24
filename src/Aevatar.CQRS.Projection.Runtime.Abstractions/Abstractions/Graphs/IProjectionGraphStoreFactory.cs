namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphStoreFactory
{
    IProjectionGraphStore Create(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements);
}
