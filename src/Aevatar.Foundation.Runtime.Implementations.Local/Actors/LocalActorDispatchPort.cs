using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActorDispatchPort : IActorDispatchPort
{
    private readonly IActorRuntime _runtime;

    public LocalActorDispatchPort(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);

        var actor = await _runtime.GetAsync(actorId)
                    ?? throw new InvalidOperationException($"Actor {actorId} not found.");
        await actor.HandleEventAsync(envelope, ct);
    }
}
