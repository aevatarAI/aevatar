namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreProviderRegistry
{
    IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> GetRegistrations(IServiceProvider serviceProvider);
}
