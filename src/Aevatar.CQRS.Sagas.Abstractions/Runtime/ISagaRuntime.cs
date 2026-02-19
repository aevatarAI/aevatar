namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaRuntime
{
    Task ObserveAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default);
}
