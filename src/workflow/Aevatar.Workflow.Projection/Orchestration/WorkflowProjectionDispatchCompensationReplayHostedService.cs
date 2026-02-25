using Aevatar.Workflow.Projection.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowProjectionDispatchCompensationReplayHostedService
    : IHostedService,
      IDisposable
{
    private readonly IProjectionDispatchCompensationOutbox _outbox;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ILogger<WorkflowProjectionDispatchCompensationReplayHostedService> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;

    public WorkflowProjectionDispatchCompensationReplayHostedService(
        IProjectionDispatchCompensationOutbox outbox,
        WorkflowExecutionProjectionOptions options,
        ILogger<WorkflowProjectionDispatchCompensationReplayHostedService>? logger = null)
    {
        _outbox = outbox;
        _options = options;
        _logger = logger ?? NullLogger<WorkflowProjectionDispatchCompensationReplayHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.EnableDispatchCompensationReplay)
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
        await _outbox.TriggerReplayAsync(_options.DispatchCompensationReplayBatchSize, ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var pollIntervalMs = Math.Max(50, _options.DispatchCompensationReplayPollIntervalMs);
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
                    "Projection compensation replay trigger failed. worker={Worker}",
                    nameof(WorkflowProjectionDispatchCompensationReplayHostedService));
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
