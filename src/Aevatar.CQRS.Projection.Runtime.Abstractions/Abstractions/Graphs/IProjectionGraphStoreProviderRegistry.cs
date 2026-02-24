namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphStoreProviderRegistry
{
    IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> GetRegistrations(IServiceProvider serviceProvider);
}
