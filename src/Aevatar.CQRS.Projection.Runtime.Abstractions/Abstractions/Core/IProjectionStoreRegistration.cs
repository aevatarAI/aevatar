namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreRegistration
{
    string ProviderName { get; }

    ProjectionProviderCapabilities Capabilities { get; }
}

public interface IProjectionStoreRegistration<out TStore> : IProjectionStoreRegistration
{
    TStore Create(IServiceProvider serviceProvider);
}
