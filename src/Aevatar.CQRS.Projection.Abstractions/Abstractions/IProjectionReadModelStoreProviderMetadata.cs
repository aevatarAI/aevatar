namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelStoreProviderMetadata
{
    ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }
}
