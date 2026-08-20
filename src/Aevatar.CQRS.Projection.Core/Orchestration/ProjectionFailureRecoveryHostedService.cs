using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionFailureRecoveryHostedService : BackgroundService
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ProjectionFailureRecoveryReconciler _reconciler;
    private readonly ILogger<ProjectionFailureRecoveryHostedService> _logger;

    public ProjectionFailureRecoveryHostedService(
        ProjectionFailureRecoveryReconciler reconciler,
        ILogger<ProjectionFailureRecoveryHostedService> logger)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Projection failure recovery sweep failed and will be retried.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
