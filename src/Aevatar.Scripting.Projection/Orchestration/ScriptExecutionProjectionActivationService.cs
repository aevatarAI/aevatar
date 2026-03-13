using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionProjectionActivationService
    : ProjectionActivationServiceBase<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    public ScriptExecutionProjectionActivationService(
        IProjectionLifecycleService<ScriptExecutionProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptExecutionProjectionContext CreateContext(
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

        return new ScriptExecutionProjectionContext
        {
            ProjectionId = $"{rootEntityId}:read-model",
            RootActorId = rootEntityId,
        };
    }

    protected override ScriptExecutionRuntimeLease CreateRuntimeLease(ScriptExecutionProjectionContext context) =>
        new(context);
}
