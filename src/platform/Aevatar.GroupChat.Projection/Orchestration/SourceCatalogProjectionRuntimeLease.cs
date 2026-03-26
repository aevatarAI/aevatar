using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GroupChat.Projection.Contexts;

namespace Aevatar.GroupChat.Projection.Orchestration;

public sealed class SourceCatalogProjectionRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<SourceCatalogProjectionContext>
{
    public SourceCatalogProjectionRuntimeLease(SourceCatalogProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
    }

    public SourceCatalogProjectionContext Context { get; }
}
