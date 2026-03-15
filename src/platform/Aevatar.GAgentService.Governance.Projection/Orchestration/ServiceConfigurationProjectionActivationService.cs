using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationProjectionActivationService
    : ProjectionActivationServiceBase<ServiceConfigurationRuntimeLease, ServiceConfigurationProjectionContext, IReadOnlyList<string>>
{
    public ServiceConfigurationProjectionActivationService(
        IProjectionLifecycleService<ServiceConfigurationProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceConfigurationProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return new ServiceConfigurationProjectionContext
        {
            ProjectionId = $"{projectionName}:{rootEntityId}",
            RootActorId = rootEntityId,
        };
    }

    protected override ServiceConfigurationRuntimeLease CreateRuntimeLease(ServiceConfigurationProjectionContext context) =>
        new(context);
}
