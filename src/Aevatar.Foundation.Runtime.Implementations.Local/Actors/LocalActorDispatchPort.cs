using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActorDispatchPort : IActorDispatchPort
{
    private readonly IActorRuntime _runtime;

    public LocalActorDispatchPort(IActorRuntime runtime, IStreamProvider streams)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(streams);
    }

    public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (await _runtime.GetAsync(actorId) is not LocalActor target)
            throw new InvalidOperationException($"Actor {actorId} not found.");

        _ = target.AcceptDispatchedEnvelopeAsync(envelope.Clone());
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
