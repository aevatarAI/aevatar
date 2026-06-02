using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (issue-377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (issue-377): Old pattern: ScopeId duplicated the room root actor id.
// Refactor (issue-377): New principle: room session context carries RootActorId + SessionId.
// Refactor (issue-377): New principle: session attach routes from typed Context.
public sealed class StreamingProxyRoomSessionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<StreamingProxyRoomSessionEnvelope>,
      IStreamingProxyRoomSessionProjectionLease,
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
}
