using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionReleaseService
    : ProjectionReleaseServiceBase<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>
{
    public ScriptEvolutionProjectionReleaseService(
        IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptEvolutionSessionProjectionContext ResolveContext(ScriptEvolutionRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
