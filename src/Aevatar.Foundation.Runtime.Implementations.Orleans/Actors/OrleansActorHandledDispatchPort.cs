using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Orleans;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorHandledDispatchPort : IActorHandledDispatchPort
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorHandledDispatchPort(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public async Task<DispatchAdmission> DispatchAndWaitHandledAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        if (!await grain.IsInitializedAsync())
            throw new InvalidOperationException($"Actor {actorId} is not initialized.");

        var handledEnvelope = envelope.Clone();
        handledEnvelope.EnsureRuntime().EnsureDispatch().PropagateFailure = true;
        await grain.HandleEnvelopeAsync(handledEnvelope.ToByteArray());
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
