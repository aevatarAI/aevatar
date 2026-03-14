using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityRuntimeLease
    : ProjectionRuntimeLeaseBase<IEventSink<EventEnvelope>>,
      IProjectionPortSessionLease
{
    public ScriptAuthorityRuntimeLease(ScriptAuthorityProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ScriptAuthorityProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;
}
