namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionRelationStoreRegistration
{
    string ProviderName { get; }

    ProjectionReadModelProviderCapabilities Capabilities { get; }

    IProjectionRelationStore Create(IServiceProvider serviceProvider);
}
