namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionRelationStoreProviderRegistry
{
    IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> GetRegistrations(IServiceProvider serviceProvider);
}
