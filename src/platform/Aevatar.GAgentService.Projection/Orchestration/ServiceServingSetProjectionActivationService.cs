using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceServingSetProjectionActivationService
    : ProjectionActivationServiceBase<ServiceServingSetRuntimeLease, ServiceServingSetProjectionContext, IReadOnlyList<string>>
{
    public ServiceServingSetProjectionActivationService(
        IProjectionLifecycleService<ServiceServingSetProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceServingSetProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceServingSetProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceServingSetRuntimeLease CreateRuntimeLease(ServiceServingSetProjectionContext context) =>
        new(context);
}
