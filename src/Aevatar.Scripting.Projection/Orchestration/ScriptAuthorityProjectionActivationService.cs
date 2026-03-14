using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionActivationService
    : ProjectionActivationServiceBase<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext, IReadOnlyList<string>>
{
    public ScriptAuthorityProjectionActivationService(
        IProjectionLifecycleService<ScriptAuthorityProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptAuthorityProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = projectionName;
        _ = input;
        _ = commandId;
        _ = ct;

        return new ScriptAuthorityProjectionContext
        {
            ProjectionId = $"{rootEntityId}:authority",
            RootActorId = rootEntityId,
        };
    }

    protected override ScriptAuthorityRuntimeLease CreateRuntimeLease(ScriptAuthorityProjectionContext context) =>
        new(context);
}
