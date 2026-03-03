using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionActivationService
    : IProjectionPortActivationService<ScriptEvolutionRuntimeLease>
{
    private readonly IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> _lifecycle;

    public ScriptEvolutionProjectionActivationService(
        IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<ScriptEvolutionRuntimeLease> EnsureAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        _ = projectionName;
        _ = input;
        ArgumentException.ThrowIfNullOrWhiteSpace(rootEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = rootEntityId,
            RootActorId = rootEntityId,
            ProposalId = commandId,
        };

        await _lifecycle.StartAsync(context, ct);
        return new ScriptEvolutionRuntimeLease(context);
    }
}
