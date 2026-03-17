using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ScriptAuthorityProjectionContext>
{
    public ScriptAuthorityRuntimeLease(ScriptAuthorityProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ScriptAuthorityProjectionContext Context { get; }
}
