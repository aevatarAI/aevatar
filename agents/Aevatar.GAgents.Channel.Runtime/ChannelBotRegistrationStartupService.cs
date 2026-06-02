using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup so the query-side read model can catch up through
/// the committed-state projection activation path after a restart.
///
/// StartAsync dispatches one activation attempt. Retry/backoff belongs to
/// actor/runtime scheduling infrastructure, and request paths must not
/// activate or prime this projection themselves.
/// </summary>
internal sealed class ChannelBotRegistrationStartupService : IHostedService
{
    // Refactor (iter99/cluster-099): Old pattern: ChannelBotRegistrationStartupService dispatched ChannelBotRebuildProjectionCommand; actor wrote no-op ChannelBotProjectionRebuildRequestedEvent to trigger projection refresh. New principle: committed-state publication + projection activation cover cold-start refresh natively; no synthetic event needed.
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual projection refresh surface, new=host startup projection activation
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=operator-triggered rebuild, new=activation-only startup warm-up
    // Refactor (iter165/cluster-001): Old pattern: startup owned a Task.Delay retry loop for projection activation. New principle: startup dispatches one bootstrap activation attempt; retry/backoff belongs to actor/runtime scheduling infrastructure, not this hosted service.

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
        try
        {
            await _projectionActivator.ActivateWellKnownCatalogAsync(ct);
            _logger.LogInformation(
                "Channel bot registration projection scope activated for {ActorId}",
                ChannelBotRegistrationGAgent.WellKnownId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
