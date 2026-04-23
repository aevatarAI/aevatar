using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup, then re-emits the authoritative state root so the
/// query-side read model can be rebuilt after a restart.
///
/// StartAsync awaits the activation with retries so the host does not
/// accept HTTP requests until the registration projection binder is active and
/// the refresh command has been accepted. Request paths must not activate or
/// prime this projection themselves.
/// </summary>
public sealed class ChannelBotRegistrationStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelBotRegistrationProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly ILogger<ChannelBotRegistrationStartupService> _logger;

    public ChannelBotRegistrationStartupService(
        ChannelBotRegistrationProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        ILogger<ChannelBotRegistrationStartupService> logger)
    {
        _projectionPort = projectionPort;
        _actorRuntime = actorRuntime;
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
                await ChannelBotRegistrationStoreCommands.DispatchRebuildProjectionAsync(
                    _actorRuntime,
                    "startup_projection_rebuild",
                    ct);
                _logger.LogInformation(
                    "Channel bot registration projection scope activated and rebuild dispatched for {ActorId} (attempt {Attempt})",
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
                    "Failed to activate or rebuild channel bot registration projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt, MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);
                delay *= 2; // exponential backoff
            }
        }

        // All retries exhausted — let the host start in degraded mode.
        // Registrations may appear missing until the projection binder and
        // authoritative refresh are re-triggered by a later host restart or
        // operator intervention.
        _logger.LogError(
            "Channel bot registration projection activation/rebuild failed after {MaxRetries} attempts — " +
            "registrations may not be visible until the refresh path is re-triggered",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
