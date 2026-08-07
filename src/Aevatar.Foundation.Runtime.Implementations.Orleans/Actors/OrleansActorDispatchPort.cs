using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorDispatchPort : IActorDispatchPort
{
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansActorDispatchPort(Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        await _streams.GetStream(actorId).ProduceAsync(envelope.Clone(), ct);
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
