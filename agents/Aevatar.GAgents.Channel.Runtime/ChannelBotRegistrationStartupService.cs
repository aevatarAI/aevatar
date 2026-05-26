using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup, then re-emits the authoritative state root so the
/// query-side read model can be refreshed after a restart.
///
/// StartAsync awaits the activation with retries so the host does not
/// accept HTTP requests until the registration projection binder is active and
/// the refresh command has been accepted. Request paths must not activate or
/// prime this projection themselves.
/// </summary>
internal sealed class ChannelBotRegistrationStartupService : IHostedService
{
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual projection refresh surface, new=startup-owned actor inbox dispatch
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=operator-triggered rebuild, new=host startup refresh after projection activation
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly ChannelBotRegistrationProjectionBootstrapActivator _projectionActivator;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorHandledDispatchPort _dispatchPort;
    private readonly ILogger<ChannelBotRegistrationStartupService> _logger;

    public ChannelBotRegistrationStartupService(
        ChannelBotRegistrationProjectionBootstrapActivator projectionActivator,
        IActorRuntime actorRuntime,
        IActorHandledDispatchPort dispatchPort,
        ILogger<ChannelBotRegistrationStartupService> logger)
    {
        _projectionActivator = projectionActivator;
        _actorRuntime = actorRuntime;
        _dispatchPort = dispatchPort;
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
                await DispatchStartupProjectionRefreshAsync(ct);
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

    private async Task DispatchStartupProjectionRefreshAsync(CancellationToken ct)
    {
        // Refactor (iter101/cluster-104): Old channel registration exposed a reusable RebuildProjection helper; new rebuild signal is private to startup after projection activation.
        _ = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new ChannelBotRebuildProjectionCommand
            {
                Reason = "startup_projection_rebuild",
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(
                "channel-runtime.registration-store",
                ChannelBotRegistrationGAgent.WellKnownId),
        };

        await _dispatchPort.DispatchAndWaitHandledAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            envelope,
            ct);
    }
}
