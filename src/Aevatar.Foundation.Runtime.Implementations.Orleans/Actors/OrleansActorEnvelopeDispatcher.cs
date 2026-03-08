using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorEnvelopeDispatcher : IActorEnvelopeDispatcher
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorEnvelopeDispatcher(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        return _grainFactory
            .GetGrain<IRuntimeActorGrain>(actorId)
            .HandleEnvelopeAsync(envelope.ToByteArray());
    }
}
