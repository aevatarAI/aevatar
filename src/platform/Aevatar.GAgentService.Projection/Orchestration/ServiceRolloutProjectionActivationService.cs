using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRolloutProjectionActivationService
    : ProjectionActivationServiceBase<ServiceRolloutRuntimeLease, ServiceRolloutProjectionContext, IReadOnlyList<string>>
{
    public ServiceRolloutProjectionActivationService(
        IProjectionLifecycleService<ServiceRolloutProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceRolloutProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceRolloutProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceRolloutRuntimeLease CreateRuntimeLease(ServiceRolloutProjectionContext context) =>
        new(context);
}
