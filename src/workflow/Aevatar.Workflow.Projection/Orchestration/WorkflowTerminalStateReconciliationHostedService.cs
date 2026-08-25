using Aevatar.Workflow.Projection.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowTerminalStateReconciliationHostedService : BackgroundService
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ILogger<WorkflowTerminalStateReconciliationHostedService> _logger;

    public WorkflowTerminalStateReconciliationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        ILogger<WorkflowTerminalStateReconciliationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<WorkflowTerminalStateReconciliationHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.EnableTerminalStateReconciliation)
            return;

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var reconciler = _serviceProvider.GetRequiredService<WorkflowTerminalStateReconciler>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Workflow terminal reconciliation sweep failed and will be retried.");
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
