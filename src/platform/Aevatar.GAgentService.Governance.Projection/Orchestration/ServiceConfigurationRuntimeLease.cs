using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ServiceConfigurationProjectionContext>
{
    public ServiceConfigurationRuntimeLease(ServiceConfigurationProjectionContext context)
        : base(GetRootActorId(context))
    {
        Context = context;
    }

    public ServiceConfigurationProjectionContext Context { get; }

    private static string GetRootActorId(ServiceConfigurationProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RootActorId;
    }
}
