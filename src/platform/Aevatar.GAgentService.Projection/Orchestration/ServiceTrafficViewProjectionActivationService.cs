using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceTrafficViewProjectionActivationService
    : ProjectionActivationServiceBase<ServiceTrafficViewRuntimeLease, ServiceTrafficViewProjectionContext, IReadOnlyList<string>>
{
    public ServiceTrafficViewProjectionActivationService(
        IProjectionLifecycleService<ServiceTrafficViewProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceTrafficViewProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceTrafficViewProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceTrafficViewRuntimeLease CreateRuntimeLease(ServiceTrafficViewProjectionContext context) =>
        new(context);
}
