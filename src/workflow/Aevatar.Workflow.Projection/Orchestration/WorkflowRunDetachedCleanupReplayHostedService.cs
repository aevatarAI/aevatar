using Aevatar.Workflow.Projection.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowRunDetachedCleanupReplayHostedService
    : IHostedService,
      IDisposable
{
    private readonly IWorkflowRunDetachedCleanupOutbox _outbox;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ILogger<WorkflowRunDetachedCleanupReplayHostedService> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;

    public WorkflowRunDetachedCleanupReplayHostedService(
        IWorkflowRunDetachedCleanupOutbox outbox,
        WorkflowExecutionProjectionOptions options,
        ILogger<WorkflowRunDetachedCleanupReplayHostedService>? logger = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<WorkflowRunDetachedCleanupReplayHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.EnableDetachedCleanupReplay)
            return Task.CompletedTask;

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runTask == null || _stoppingCts == null)
            return;

        _stoppingCts.Cancel();
        var completed = await Task.WhenAny(
            _runTask,
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        if (!ReferenceEquals(completed, _runTask))
            throw new OperationCanceledException(cancellationToken);

        await _runTask;
    }

    public void Dispose()
    {
        _stoppingCts?.Dispose();
    }

    internal async Task ReplayOnceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _outbox.TriggerReplayAsync(_options.DetachedCleanupReplayBatchSize, ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var pollIntervalMs = Math.Max(50, _options.DetachedCleanupReplayPollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReplayOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Detached workflow cleanup replay trigger failed. worker={Worker}",
                    nameof(WorkflowRunDetachedCleanupReplayHostedService));
            }

            try
            {
                await Task.Delay(pollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
