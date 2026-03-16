using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionDispatchCompensationReplayHostedService
    : IHostedService,
      IDisposable
{
    private readonly IProjectionDispatchCompensationOutbox _outbox;
    private readonly IProjectionDispatchCompensationOptions _options;
    private readonly ILogger<ProjectionDispatchCompensationReplayHostedService> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;

    public ProjectionDispatchCompensationReplayHostedService(
        IProjectionDispatchCompensationOutbox outbox,
        IProjectionDispatchCompensationOptions options,
        ILogger<ProjectionDispatchCompensationReplayHostedService>? logger = null)
    {
        _outbox = outbox;
        _options = options;
        _logger = logger ?? NullLogger<ProjectionDispatchCompensationReplayHostedService>.Instance;
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

    public async Task ReplayOnceAsync(CancellationToken ct = default)
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
                    nameof(ProjectionDispatchCompensationReplayHostedService));
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
