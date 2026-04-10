using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Activates the projection scope for the channel bot registration store
/// at application startup. Without this, the scope agent never subscribes
/// to the actor's event stream after a restart, and old registrations
/// are lost from the InMemory store.
/// </summary>
public sealed class ChannelBotRegistrationStartupService : IHostedService
{
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
        try
        {
            await _projectionPort.EnsureProjectionForActorAsync(
                ChannelBotRegistrationGAgent.WellKnownId, ct);
            _logger.LogInformation(
                "Channel bot registration projection scope activated for {ActorId}",
                ChannelBotRegistrationGAgent.WellKnownId);
        }
        catch (Exception ex)
        {
            // Non-fatal: the scope can be activated later during registration
            _logger.LogWarning(ex,
                "Failed to activate channel bot registration projection scope at startup");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
