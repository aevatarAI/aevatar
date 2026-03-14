using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceEndpointCatalogRuntimeLease
    : ProjectionRuntimeLeaseBase<IEventSink<EventEnvelope>>,
      IProjectionPortSessionLease
{
    public ServiceEndpointCatalogRuntimeLease(ServiceEndpointCatalogProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ServiceEndpointCatalogProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;
}
