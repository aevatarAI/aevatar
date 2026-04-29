using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Default <see cref="IStudioActorBootstrap"/> implementation. Uses
/// <see cref="IActorRuntime"/> to ensure the actor, then
/// <see cref="StudioProjectionPort"/> to activate the materialization
/// scope for <see cref="IProjectedActor.ProjectionKind"/>. Idempotent on
/// both steps.
/// </summary>
internal sealed class StudioActorBootstrap : IStudioActorBootstrap
{
    private readonly IActorRuntime _runtime;
    private readonly StudioProjectionPort _projectionPort;

    public StudioActorBootstrap(IActorRuntime runtime, StudioProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var actor = await _runtime.GetAsync(actorId)
                    ?? await _runtime.CreateAsync<TAgent>(actorId, ct);

        await _projectionPort.EnsureProjectionAsync(actorId, TAgent.ProjectionKind, ct);

        return actor;
    }

    public async Task<IActor?> GetExistingAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var actor = await _runtime.GetAsync(actorId);
        if (actor is null)
            return null;

        await _projectionPort.EnsureProjectionAsync(actorId, TAgent.ProjectionKind, ct);
        return actor;
    }

    public Task<IActor?> GetExistingActorAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return _runtime.GetAsync(actorId);
    }
}
