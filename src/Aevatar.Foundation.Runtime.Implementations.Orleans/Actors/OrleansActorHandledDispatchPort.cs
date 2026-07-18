using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Google.Protobuf;
using Orleans;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorHandledDispatchPort : IActorHandledDispatchPort
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorHandledDispatchPort(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public async Task<DispatchAdmission> DispatchHandledAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var normalizedActorId = actorId.Trim();
        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(normalizedActorId);
        if (!await grain.IsInitializedAsync().ConfigureAwait(false))
            throw new ActorNotFoundException(normalizedActorId);

        var handledEnvelope = envelope.Clone();
        handledEnvelope.EnsureRuntime().EnsureDispatch().PropagateFailure = true;
        await grain.HandleEnvelopeAsync(handledEnvelope.ToByteArray()).ConfigureAwait(false);
        return DispatchAdmissionFactory.Create(normalizedActorId, handledEnvelope);
    }
}
