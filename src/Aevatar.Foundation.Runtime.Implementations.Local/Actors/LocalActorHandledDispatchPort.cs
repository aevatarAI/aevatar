using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActorHandledDispatchPort : IActorHandledDispatchPort
{
    private readonly IActorRuntime _runtime;

    public LocalActorHandledDispatchPort(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
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
        var target = await _runtime.GetAsync(normalizedActorId).ConfigureAwait(false);
        if (target is null)
            throw new ActorNotFoundException(normalizedActorId);

        var handledEnvelope = envelope.Clone();
        handledEnvelope.EnsureRuntime().EnsureDispatch().PropagateFailure = true;
        await target.HandleEventAsync(handledEnvelope, ct).ConfigureAwait(false);
        return DispatchAdmissionFactory.Create(normalizedActorId, handledEnvelope);
    }
}
