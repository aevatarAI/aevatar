using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Propagation;
using Orleans;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorDispatchPort : IActorDispatchPort
{
    private readonly IGrainFactory _grainFactory;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansActorDispatchPort(
        IGrainFactory grainFactory,
        Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        if (!await grain.IsInitializedAsync())
            throw new InvalidOperationException($"Actor {actorId} is not initialized.");

        await _streams.GetStream(actorId).ProduceAsync(
            EnvelopeDeliveryProvenanceSemantics.CloneForRawDispatch(envelope),
            ct);
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
