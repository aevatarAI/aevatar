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

        if (envelope.Runtime?.Dispatch?.RequireTargetActorAdmission != true)
        {
            await _streams.GetStream(actorId).ProduceAsync(envelope.Clone(), ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var currentGrainId = _grainContextAccessor.GrainContext?.GrainId;
        if (currentGrainId is not null && currentGrainId.Equals(grain.GetGrainId()))
        {
            // A self continuation must enter the next actor turn. Calling the current grain
            // recursively would either deadlock activation or execute inline, so self delivery
            // keeps the durable stream handoff used by GAgent publication.
            await _streams.GetStream(actorId).ProduceAsync(envelope.Clone(), ct);
        }
        else
        {
            // The target grain owns admission: it proves that the actor is initialized and
            // publishes to its own stream from a serialized actor turn. This closes the gap
            // where a producer-only Kafka acknowledgement was reported as actor-inbox admission.
            await grain.AdmitEnvelopeAsync(envelope.ToByteArray()).WaitAsync(ct);
        }

        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
