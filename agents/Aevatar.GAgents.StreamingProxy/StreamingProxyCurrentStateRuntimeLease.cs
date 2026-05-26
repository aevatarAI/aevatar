using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyCurrentStateRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<StreamingProxyCurrentStateProjectionContext>
{
    public StreamingProxyCurrentStateRuntimeLease(StreamingProxyCurrentStateProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public StreamingProxyCurrentStateProjectionContext Context { get; }
}
