namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>
/// Dispatches deactivation lifecycle hooks for one actor.
/// </summary>
public interface IActorDeactivationHookDispatcher
{
    Task DispatchAsync(string actorId, CancellationToken ct = default);
}
