using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServicePolicyProjectionActivationService
    : ProjectionActivationServiceBase<ServicePolicyRuntimeLease, ServicePolicyProjectionContext, IReadOnlyList<string>>
{
    public ServicePolicyProjectionActivationService(
        IProjectionLifecycleService<ServicePolicyProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServicePolicyProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServicePolicyProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServicePolicyRuntimeLease CreateRuntimeLease(ServicePolicyProjectionContext context) =>
        new(context);
}
