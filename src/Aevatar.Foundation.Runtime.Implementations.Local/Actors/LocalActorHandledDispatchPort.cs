using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActorHandledDispatchPort : IActorHandledDispatchPort
{
    private readonly IActorRuntime _runtime;

    public LocalActorHandledDispatchPort(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<DispatchAdmission> DispatchAndWaitHandledAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var actor = await _runtime.GetAsync(actorId)
            ?? throw new InvalidOperationException($"Actor {actorId} not found.");

        await actor.HandleEventAsync(envelope.Clone(), ct);
        return DispatchAdmissionFactory.Create(actorId, envelope);
    }
}
