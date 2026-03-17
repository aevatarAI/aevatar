using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ScriptEvolutionMaterializationContext>
{
    public ScriptEvolutionMaterializationRuntimeLease(ScriptEvolutionMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ScriptEvolutionMaterializationContext Context { get; }
}
