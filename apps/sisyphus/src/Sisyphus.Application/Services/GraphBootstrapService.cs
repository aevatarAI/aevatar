using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sisyphus.Application.Services;

/// <summary>
/// On startup, resolves the "sisyphus" graph UUID from ChronoGraph.
/// If the graph doesn't exist yet, creates it once. Stores the UUID in
/// <see cref="GraphIdProvider"/> so the rest of the app can use it.
/// </summary>
public sealed class GraphBootstrapService(
    ChronoGraphClient chronoGraph,
    GraphIdProvider graphIdProvider,
    ILogger<GraphBootstrapService> logger) : BackgroundService
{
    private const int MaxRetries = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Try to find an existing graph named "sisyphus"
                var graphId = await chronoGraph.FindGraphIdByNameAsync(
                    GraphIdProvider.GraphName, stoppingToken);

                if (graphId is not null)
                {
                    graphIdProvider.Set(graphId);
                    logger.LogInformation(
                        "Graph '{Name}' found with id {Id}", GraphIdProvider.GraphName, graphId);
                    return;
                }

                // Doesn't exist yet — create it
                graphId = await chronoGraph.CreateGraphAsync(
                    GraphIdProvider.GraphName, stoppingToken);

                graphIdProvider.Set(graphId);
                logger.LogInformation(
                    "Graph '{Name}' created with id {Id}", GraphIdProvider.GraphName, graphId);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 30));
                logger.LogWarning(ex,
                    "ChronoGraph not reachable (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }

        logger.LogError(
            "Could not resolve graph '{Name}' after {Max} attempts — graph operations will fail",
            GraphIdProvider.GraphName, MaxRetries);
    }
}
