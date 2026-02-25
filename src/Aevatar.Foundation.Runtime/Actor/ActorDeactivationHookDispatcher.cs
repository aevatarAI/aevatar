using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>
/// Executes all registered actor deactivation hooks.
/// Hook failures are isolated and logged without breaking deactivation path.
/// </summary>
public sealed class ActorDeactivationHookDispatcher : IActorDeactivationHookDispatcher
{
    private readonly IReadOnlyList<IActorDeactivationHook> _hooks;
    private readonly ILogger<ActorDeactivationHookDispatcher> _logger;

    public ActorDeactivationHookDispatcher(
        IEnumerable<IActorDeactivationHook> hooks,
        ILogger<ActorDeactivationHookDispatcher>? logger = null)
    {
        _hooks = hooks.ToArray();
        _logger = logger ?? NullLogger<ActorDeactivationHookDispatcher>.Instance;
    }

    public async Task DispatchAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        for (var i = 0; i < _hooks.Count; i++)
        {
            var hook = _hooks[i];
            try
            {
                await hook.OnDeactivatedAsync(actorId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Actor deactivation hook failed and was ignored. actorId={ActorId} hookType={HookType}",
                    actorId,
                    hook.GetType().FullName);
            }
        }
    }
}
