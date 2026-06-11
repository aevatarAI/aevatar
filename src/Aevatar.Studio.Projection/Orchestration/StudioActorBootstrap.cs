using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Default <see cref="IStudioActorBootstrap"/> implementation. Uses
/// <see cref="IActorRuntime"/> only to ensure the actor exists.
/// </summary>
internal sealed class StudioActorBootstrap : IStudioActorBootstrap
{
    private readonly IActorRuntime _runtime;

    public StudioActorBootstrap(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   Studio bootstrap now provisions only the target actor.
        var actor = await _runtime.GetAsync(actorId)
                    ?? await _runtime.CreateAsync<TAgent>(actorId, ct);

        return actor;
    }
}
