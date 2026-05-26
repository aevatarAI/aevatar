using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<StreamingProxyRoomSessionEnvelope>,
      IStreamingProxyRoomSessionProjectionLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<StreamingProxyRoomSessionProjectionContext>
{
    public StreamingProxyRoomSessionRuntimeLease(StreamingProxyRoomSessionProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string SessionId { get; }

    public StreamingProxyRoomSessionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;
}
