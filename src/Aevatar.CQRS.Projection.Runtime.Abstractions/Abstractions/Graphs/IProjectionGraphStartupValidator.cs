namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphStartupValidator
{
    IProjectionStoreRegistration<IProjectionGraphStore> ValidateProvider(
        IServiceProvider serviceProvider,
        ProjectionGraphSelectionOptions selectionOptions);
}
