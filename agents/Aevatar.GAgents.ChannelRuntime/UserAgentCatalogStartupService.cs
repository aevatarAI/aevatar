using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogStartupService : IHostedService
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly UserAgentCatalogProjectionPort _projectionPort;
    private readonly ILogger<UserAgentCatalogStartupService> _logger;

    public UserAgentCatalogStartupService(
        UserAgentCatalogProjectionPort projectionPort,
        ILogger<UserAgentCatalogStartupService> logger)
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
                await _projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);
                _logger.LogInformation(
                    "User agent catalog projection scope activated for {ActorId} (attempt {Attempt})",
                    UserAgentCatalogGAgent.WellKnownId,
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
                    "Failed to activate user agent catalog projection scope (attempt {Attempt}/{MaxRetries})",
                    attempt,
                    MaxRetries);

                if (attempt < MaxRetries)
                    await Task.Delay(delay, ct);

                delay *= 2;
            }
        }

        _logger.LogError(
            "User agent catalog projection scope activation failed after {MaxRetries} attempts",
            MaxRetries);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
