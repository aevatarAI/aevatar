namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionRelationStoreFactory
{
    IProjectionRelationStore Create(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
