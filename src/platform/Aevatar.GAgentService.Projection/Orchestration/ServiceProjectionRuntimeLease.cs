using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionRuntimeLease<TContext>
    : ProjectionRuntimeLeaseBase<IEventSink<EventEnvelope>>,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionContext
{
    public ServiceProjectionRuntimeLease(string rootEntityId, TContext context)
        : base(rootEntityId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public TContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;
}
