using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionActivationService
    : ProjectionActivationServiceBase<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>
{
    public ScriptEvolutionProjectionActivationService(
        IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ScriptEvolutionSessionProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = projectionName;
        _ = input;
        _ = ct;
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        return new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = rootEntityId,
            RootActorId = rootEntityId,
            ProposalId = commandId,
        };
    }

    protected override ScriptEvolutionRuntimeLease CreateRuntimeLease(ScriptEvolutionSessionProjectionContext context) =>
        new(context);
}
