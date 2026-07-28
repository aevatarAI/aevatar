using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.Studio.Workspace;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

internal sealed class ActorDispatchStudioWorkspaceProjectionRepublishPort
    : IStudioWorkspaceProjectionRepublishPort
{
    private const string PublisherId =
        "aevatar.studio.infrastructure.workspace-projection-repair";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;

    public ActorDispatchStudioWorkspaceProjectionRepublishPort(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async Task<StudioWorkspaceProjectionRepublishReceipt> DispatchAsync(
        string scopeId,
        long minimumStateVersion,
        string repairRequestId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioWorkspaceConventions.NormalizeScopeId(scopeId);
        if (minimumStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumStateVersion),
                "Minimum state version must be positive.");
        }

        var normalizedRepairRequestId = repairRequestId?.Trim() ?? string.Empty;
        if (normalizedRepairRequestId.Length == 0)
            throw new ArgumentException("repairRequestId is required.", nameof(repairRequestId));

        var actorId = StudioWorkspaceConventions.BuildActorId(normalizedScopeId);
        var actor = await _bootstrap.EnsureAsync<StudioWorkspaceGAgent>(actorId, ct);
        var receipt = await _commandDispatch.DispatchAsync(
            actor,
            new RepairStudioWorkspaceProjectionCommand
            {
                WorkspaceId = actorId,
                ScopeId = normalizedScopeId,
                MinimumStateVersion = minimumStateVersion,
                RepairRequestId = normalizedRepairRequestId,
            },
            PublisherId,
            ct);
        return new StudioWorkspaceProjectionRepublishReceipt(
            receipt.ActorId,
            receipt.CommandId,
            receipt.CorrelationId);
    }
}
