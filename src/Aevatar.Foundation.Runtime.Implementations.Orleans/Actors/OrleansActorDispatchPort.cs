using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Orleans;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorDispatchPort : IActorDispatchPort
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorDispatchPort(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        if (!await grain.IsInitializedAsync())
            throw new InvalidOperationException($"Actor {actorId} is not initialized.");

        var dispatchEnvelope = envelope.Clone();
        dispatchEnvelope.EnsureRuntime().EnsureDispatch().PropagateFailure = true;
        await grain.HandleEnvelopeAsync(dispatchEnvelope.ToByteArray());
    }
}
