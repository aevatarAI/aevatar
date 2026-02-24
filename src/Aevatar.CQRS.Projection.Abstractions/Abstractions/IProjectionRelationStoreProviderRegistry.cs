namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreProviderRegistry
{
    IReadOnlyList<IProjectionRelationStoreRegistration> GetRegistrations(IServiceProvider serviceProvider);
}
