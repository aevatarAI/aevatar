using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceDeploymentCatalogProjectionActivationService
    : ProjectionActivationServiceBase<ServiceDeploymentCatalogRuntimeLease, ServiceDeploymentCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceDeploymentCatalogProjectionActivationService(
        IProjectionLifecycleService<ServiceDeploymentCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceDeploymentCatalogProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceDeploymentCatalogProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceDeploymentCatalogRuntimeLease CreateRuntimeLease(ServiceDeploymentCatalogProjectionContext context) =>
        new(context);
}
