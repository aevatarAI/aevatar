using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup. Without this, the scope agent never subscribes
/// to the actor's event stream after a restart, and old registrations
/// are lost from the InMemory store.
///
/// StartAsync awaits the activation with retries so the host does not
/// accept HTTP requests until the registration read-model is populated.
/// This eliminates the race where webhooks arrive before the projection
/// is ready and get rejected with 404.
/// </summary>
public sealed class ChannelBotRegistrationStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelBotRegistrationProjectionPort _projectionPort;
    private readonly ILogger<ChannelBotRegistrationStartupService> _logger;

    public ChannelBotRegistrationStartupService(
        ChannelBotRegistrationProjectionPort projectionPort,
        ILogger<ChannelBotRegistrationStartupService> logger)
    {
        _projectionPort = projectionPort;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var delay = InitialDelay;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _projectionPort.EnsureProjectionForActorAsync(
                    ChannelBotRegistrationGAgent.WellKnownId, ct);
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
        // Registrations will appear missing until a new register call
        // re-activates the projection scope.
        _logger.LogError(
            "Channel bot registration projection scope activation failed after {MaxRetries} attempts — " +
            "registrations may not be visible until a new registration triggers re-activation",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
