using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<EventEnvelope>,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<ServiceConfigurationProjectionContext>
{
    public ServiceConfigurationRuntimeLease(ServiceConfigurationProjectionContext context)
        : base(GetRootActorId(context))
    {
        Context = context;
    }

    public ServiceConfigurationProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;

    private static string GetRootActorId(ServiceConfigurationProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RootActorId;
    }
}
