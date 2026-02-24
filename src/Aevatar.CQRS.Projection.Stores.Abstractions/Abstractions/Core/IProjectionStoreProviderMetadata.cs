namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionStoreProviderMetadata
{
    ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }
}
