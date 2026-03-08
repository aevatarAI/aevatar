namespace Aevatar.Foundation.Abstractions;

public interface IActorEnvelopeDispatcher
{
    Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default);
}
