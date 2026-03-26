using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class AgentFeedProjectionRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<AgentFeedProjectionContext>
{
    public AgentFeedProjectionRuntimeLease(AgentFeedProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
    }

    public AgentFeedProjectionContext Context { get; }
}
