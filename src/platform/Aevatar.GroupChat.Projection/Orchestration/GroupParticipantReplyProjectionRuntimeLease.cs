using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class GroupParticipantReplyProjectionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<GroupParticipantReplyCompletedEvent>,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<GroupParticipantReplyProjectionContext>
{
    public GroupParticipantReplyProjectionRuntimeLease(GroupParticipantReplyProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
    }

    public string ActorId => RootEntityId;

    public GroupParticipantReplyProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => Context.SessionId;
}
