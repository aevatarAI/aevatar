using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRevisionCatalogProjectionActivationService
    : ProjectionActivationServiceBase<ServiceRevisionCatalogRuntimeLease, ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceRevisionCatalogProjectionActivationService(
        IProjectionLifecycleService<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceRevisionCatalogProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceRevisionCatalogProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceRevisionCatalogRuntimeLease CreateRuntimeLease(ServiceRevisionCatalogProjectionContext context) =>
        new(context);
}
