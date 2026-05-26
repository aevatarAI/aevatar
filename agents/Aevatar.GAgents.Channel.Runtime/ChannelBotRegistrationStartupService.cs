using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup so the query-side read model can catch up through
/// the committed-state projection activation path after a restart.
///
/// StartAsync awaits the activation with retries so the host does not
/// accept HTTP requests until the registration projection binder is active.
/// Request paths must not activate or prime this projection themselves.
/// </summary>
internal sealed class ChannelBotRegistrationStartupService : IHostedService
{
    // Refactor (iter99/cluster-099): Old pattern: ChannelBotRegistrationStartupService dispatched ChannelBotRebuildProjectionCommand; actor wrote no-op ChannelBotProjectionRebuildRequestedEvent to trigger projection refresh. New principle: committed-state publication + projection activation cover cold-start refresh natively; no synthetic event needed.
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual projection refresh surface, new=host startup projection activation
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=operator-triggered rebuild, new=activation-only startup warm-up
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelBotRegistrationProjectionBootstrapActivator _projectionActivator;
    private readonly ILogger<ChannelBotRegistrationStartupService> _logger;

    public ChannelBotRegistrationStartupService(
        ChannelBotRegistrationProjectionBootstrapActivator projectionActivator,
        ILogger<ChannelBotRegistrationStartupService> logger)
    {
        _projectionActivator = projectionActivator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var delay = InitialDelay;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _projectionActivator.ActivateWellKnownCatalogAsync(ct);
                _logger.LogInformation(
                    "Channel bot registration projection scope activated for {ActorId} (attempt {Attempt})",
                    ChannelBotRegistrationGAgent.WellKnownId, attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to activate channel bot registration projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt, MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);
                delay *= 2; // exponential backoff
            }
        }

        // All retries exhausted — let the host start in degraded mode.
        // Registrations may appear missing until the projection binder is
        // activated by a later host restart or operator intervention.
        _logger.LogError(
            "Channel bot registration projection activation failed after {MaxRetries} attempts — " +
            "registrations may not be visible until activation is re-triggered",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
