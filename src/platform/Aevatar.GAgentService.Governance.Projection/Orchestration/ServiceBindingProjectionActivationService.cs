using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceBindingProjectionActivationService
    : ProjectionActivationServiceBase<ServiceBindingRuntimeLease, ServiceBindingProjectionContext, IReadOnlyList<string>>
{
    public ServiceBindingProjectionActivationService(
        IProjectionLifecycleService<ServiceBindingProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceBindingProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceBindingProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceBindingRuntimeLease CreateRuntimeLease(ServiceBindingProjectionContext context) =>
        new(context);
}
