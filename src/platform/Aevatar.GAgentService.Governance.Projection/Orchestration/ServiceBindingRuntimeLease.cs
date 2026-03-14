using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceBindingRuntimeLease
    : ProjectionRuntimeLeaseBase<IEventSink<EventEnvelope>>,
      IProjectionPortSessionLease
{
    public ServiceBindingRuntimeLease(ServiceBindingProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ServiceBindingProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;
}
