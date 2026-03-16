using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Activation;

public abstract class ActorTargetProvisionerBase
{
    private readonly IActorRuntime _runtime;

    protected ActorTargetProvisionerBase(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    protected async Task<string> EnsureActorAsync<TAgent>(string actorId, CancellationToken ct)
        where TAgent : IAgent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        if (!await _runtime.ExistsAsync(actorId))
        {
            _ = await _runtime.CreateAsync<TAgent>(actorId, ct);
            return actorId;
        }

        _ = await _runtime.GetAsync(actorId)
            ?? throw new InvalidOperationException($"Actor '{actorId}' was not found after existence check.");
        return actorId;
    }
}
