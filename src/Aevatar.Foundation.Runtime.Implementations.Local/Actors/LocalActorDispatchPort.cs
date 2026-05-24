using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActorDispatchPort : IActorDispatchPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;

    public LocalActorDispatchPort(IActorRuntime runtime, IStreamProvider streams)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (await _runtime.GetAsync(actorId) == null)
            throw new InvalidOperationException($"Actor {actorId} not found.");

        await _streams.GetStream(actorId).ProduceAsync(envelope.Clone(), ct);
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
