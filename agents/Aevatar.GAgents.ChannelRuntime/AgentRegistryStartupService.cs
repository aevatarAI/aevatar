using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class AgentRegistryStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly AgentRegistryProjectionPort _projectionPort;
    private readonly ILogger<AgentRegistryStartupService> _logger;

    public AgentRegistryStartupService(
        AgentRegistryProjectionPort projectionPort,
        ILogger<AgentRegistryStartupService> logger)
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
                await _projectionPort.EnsureProjectionForActorAsync(AgentRegistryGAgent.WellKnownId, ct);
                _logger.LogInformation(
                    "Agent registry projection scope activated for {ActorId} (attempt {Attempt})",
                    AgentRegistryGAgent.WellKnownId,
                    attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to activate agent registry projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt,
                    MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);

                delay *= 2;
            }
        }

        _logger.LogError(
            "Agent registry projection scope activation failed after {MaxRetries} attempts",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
