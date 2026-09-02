using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.WorkOrders;

public sealed class WorkOrderExecutionWorker : BackgroundService
{
    private readonly IWorkOrderExecutionQueue _queue;
    private readonly WorkOrderExecutionService _executionService;
    private readonly WorkOrderExecutionWorkerOptions _options;
    private readonly ILogger<WorkOrderExecutionWorker> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly int _maxConcurrency;
    private bool _shutdownDrainTimedOut;

    public WorkOrderExecutionWorker(
        IWorkOrderExecutionQueue queue,
        WorkOrderExecutionService executionService,
        IOptions<WorkOrderExecutionWorkerOptions> options,
        ILogger<WorkOrderExecutionWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxConcurrency = _options.MaxConcurrency > 0 ? _options.MaxConcurrency : 1;
        _concurrency = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                _ = RunOneAsync(request).ContinueWith(
                    static (_, state) => ((SemaphoreSlim)state!).Release(),
                    _concurrency,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown; in-flight execution drains below.
        }
        finally
        {
            _queue.CompleteAdding();
        }

        await DrainInFlightAsync().ConfigureAwait(false);
    }

    private async Task RunOneAsync(WorkOrderExecutionRequest request)
    {
        try
        {
            await _executionService.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Off-actor WorkOrder execution faulted for work order {WorkOrderId} command {DispatchCommandId}.",
                request.WorkOrderId,
                request.DispatchCommandId);
        }
    }

    private async Task DrainInFlightAsync()
    {
        using var graceCts = new CancellationTokenSource(_options.ShutdownDrainGrace);
        var acquired = 0;
        try
        {
            for (; acquired < _maxConcurrency; acquired++)
                await _concurrency.WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _shutdownDrainTimedOut = true;
            _logger.LogWarning(
                "WorkOrder execution worker shutdown grace ({GraceSeconds}s) elapsed with {Running} execution(s) still in flight; the WorkOrder watchdog remains authoritative.",
                _options.ShutdownDrainGraceSeconds,
                _maxConcurrency - acquired);
        }
        finally
        {
            if (acquired > 0)
                _concurrency.Release(acquired);
        }
    }

    public override void Dispose()
    {
        if (!_shutdownDrainTimedOut)
            _concurrency.Dispose();
        base.Dispose();
    }
}
