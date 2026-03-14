namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Actor envelope dispatch contract.
/// </summary>
public interface IActorDispatchPort
{
    /// <summary>Dispatches an envelope to the specified actor.</summary>
    Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default);
}
