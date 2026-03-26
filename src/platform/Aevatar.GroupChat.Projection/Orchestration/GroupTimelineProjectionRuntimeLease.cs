using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class GroupTimelineProjectionRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<GroupTimelineProjectionContext>
{
    public GroupTimelineProjectionRuntimeLease(GroupTimelineProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
    }

    public GroupTimelineProjectionContext Context { get; }
}
