using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ScriptExecutionMaterializationContext>
{
    public ScriptExecutionMaterializationRuntimeLease(ScriptExecutionMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ScriptExecutionMaterializationContext Context { get; }
}
