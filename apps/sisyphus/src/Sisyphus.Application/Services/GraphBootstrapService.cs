using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sisyphus.Application.Services;

/// <summary>
/// On startup, reads the configured graph UUIDs and stores them in
/// <see cref="GraphIdProvider"/> so the rest of the app can use them.
/// </summary>
public sealed class GraphBootstrapService(
    GraphIdProvider graphIdProvider,
    IOptions<SisyphusGraphOptions> graphOptions,
    ILogger<GraphBootstrapService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = graphOptions.Value;

        if (string.IsNullOrWhiteSpace(opts.ReadGraphId))
        {
            logger.LogError("Sisyphus:Graph:ReadGraphId is not configured — read operations will fail");
        }
        else
        {
            logger.LogInformation("Graph [read] configured: {Id}", opts.ReadGraphId);
            graphIdProvider.SetRead(opts.ReadGraphId);
        }

        if (string.IsNullOrWhiteSpace(opts.WriteGraphId))
        {
            logger.LogError("Sisyphus:Graph:WriteGraphId is not configured — write operations will fail");
        }
        else
        {
            logger.LogInformation("Graph [write] configured: {Id}", opts.WriteGraphId);
            graphIdProvider.SetWrite(opts.WriteGraphId);
        }

        return Task.CompletedTask;
    }
}
