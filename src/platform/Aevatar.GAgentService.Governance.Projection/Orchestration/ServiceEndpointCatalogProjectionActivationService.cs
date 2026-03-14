using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceEndpointCatalogProjectionActivationService
    : ProjectionActivationServiceBase<ServiceEndpointCatalogRuntimeLease, ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceEndpointCatalogProjectionActivationService(
        IProjectionLifecycleService<ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceEndpointCatalogProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceEndpointCatalogProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceEndpointCatalogRuntimeLease CreateRuntimeLease(ServiceEndpointCatalogProjectionContext context) =>
        new(context);
}
