using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionRuntimeLease<TContext>
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    public ServiceProjectionRuntimeLease(string rootEntityId, TContext context)
        : base(rootEntityId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public TContext Context { get; }
}
