using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionReleaseService
    : ProjectionReleaseServiceBase<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext, IReadOnlyList<string>>
{
    public ScriptAuthorityProjectionReleaseService(
        IProjectionLifecycleService<ScriptAuthorityProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptAuthorityProjectionContext ResolveContext(ScriptAuthorityRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
