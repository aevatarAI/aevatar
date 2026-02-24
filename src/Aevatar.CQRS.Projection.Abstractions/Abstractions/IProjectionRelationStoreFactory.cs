namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreFactory
{
    IProjectionRelationStore Create(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
