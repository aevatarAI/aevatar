using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionProjectionReleaseService
    : ProjectionReleaseServiceBase<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    public ScriptExecutionProjectionReleaseService(
        IProjectionLifecycleService<ScriptExecutionProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptExecutionProjectionContext ResolveContext(ScriptExecutionRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
