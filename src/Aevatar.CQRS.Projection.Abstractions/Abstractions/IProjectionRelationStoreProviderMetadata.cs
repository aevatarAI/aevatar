namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreProviderMetadata
{
    ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }
}
