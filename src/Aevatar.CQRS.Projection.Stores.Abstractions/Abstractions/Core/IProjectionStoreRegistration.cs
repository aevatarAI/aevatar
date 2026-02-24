namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionStoreRegistration
{
    string ProviderName { get; }

    ProjectionReadModelProviderCapabilities Capabilities { get; }
}

public interface IProjectionStoreRegistration<out TStore> : IProjectionStoreRegistration
{
    TStore Create(IServiceProvider serviceProvider);
}
