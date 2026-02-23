namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>
/// Runtime lifecycle hook invoked after actor deactivation.
/// Used to execute non-critical async idle tasks.
/// </summary>
public interface IActorDeactivationHook
{
    Task OnDeactivatedAsync(string actorId, CancellationToken ct = default);
}
