using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorDispatchPort : IActorDispatchPort
{
    private readonly IGrainFactory _grainFactory;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;
    private readonly IGrainContextAccessor _grainContextAccessor;

    public OrleansActorDispatchPort(
        IGrainFactory grainFactory,
        Aevatar.Foundation.Abstractions.IStreamProvider streams,
        IGrainContextAccessor grainContextAccessor)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _grainContextAccessor = grainContextAccessor ?? throw new ArgumentNullException(nameof(grainContextAccessor));
    }

    public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var currentGrainId = _grainContextAccessor.GrainContext?.GrainId;
        var isSelfDispatch = currentGrainId is not null && currentGrainId.Equals(grain.GetGrainId());
        if (!isSelfDispatch && !await grain.IsInitializedAsync())
            throw new InvalidOperationException($"Actor {actorId} is not initialized.");

        await _streams.GetStream(actorId).ProduceAsync(envelope.Clone(), ct);
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
