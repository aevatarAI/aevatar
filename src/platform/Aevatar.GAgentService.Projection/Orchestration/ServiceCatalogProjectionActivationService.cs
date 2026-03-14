using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceCatalogProjectionActivationService
    : ProjectionActivationServiceBase<ServiceCatalogRuntimeLease, ServiceCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceCatalogProjectionActivationService(
        IProjectionLifecycleService<ServiceCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceCatalogProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceCatalogProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceCatalogRuntimeLease CreateRuntimeLease(ServiceCatalogProjectionContext context) =>
        new(context);
}
