using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.WorkflowDeliveries;

public sealed class WorkflowDeliveryContinuationWorker : BackgroundService
{
    private readonly WorkflowDeliveryContinuationScanner _scanner;
    private readonly WorkflowDeliveryContinuationWorkerOptions _options;
    private readonly ILogger<WorkflowDeliveryContinuationWorker> _logger;

    public WorkflowDeliveryContinuationWorker(
        WorkflowDeliveryContinuationScanner scanner,
        IOptions<WorkflowDeliveryContinuationWorkerOptions> options,
        ILogger<WorkflowDeliveryContinuationWorker> logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _scanner.ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Workflow delivery continuation scan failed; failureType={FailureType}.",
                    exception.GetType().Name);
            }

            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }
}
