namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionStoreProviderMetadata
{
    ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }
}
