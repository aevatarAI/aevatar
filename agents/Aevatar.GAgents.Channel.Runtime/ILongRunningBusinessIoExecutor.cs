using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Runtime-owned bounded executor for business IO that must continue after an actor turn records intent.
/// </summary>
public interface ILongRunningBusinessIoExecutor
{
    Task SubmitAsync(LongRunningBusinessIoWorkItem workItem, CancellationToken ct);
}

public sealed record LongRunningBusinessIoWorkItem(
    string WorkItemId,
    string OwnerActorId,
    string OperationName,
    string CorrelationId,
    TimeSpan Timeout,
    Func<CancellationToken, Task> ExecuteAsync);

public sealed class LongRunningBusinessIoExecutorOptions
{
    public int MaxConcurrency { get; set; } = 8;

    public int QueueCapacity { get; set; } = 256;

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

// Refactor (iter97/cluster-098): Old pattern: actor code launched raw Task.Run business IO from the actor turn.
// New principle: actor records intent + timeout, then submits typed bounded work to this runtime-owned executor.
public sealed class LongRunningBusinessIoExecutor :
    ILongRunningBusinessIoExecutor,
    IDisposable,
    IAsyncDisposable
{
    private readonly Channel<LongRunningBusinessIoWorkItem> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task[] _workers;
    private readonly LongRunningBusinessIoExecutorOptions _options;
    private readonly ILogger<LongRunningBusinessIoExecutor> _logger;
    private bool _disposed;

    public LongRunningBusinessIoExecutor(
        IOptions<LongRunningBusinessIoExecutorOptions>? options,
        ILogger<LongRunningBusinessIoExecutor> logger)
    {
        _options = options?.Value ?? new LongRunningBusinessIoExecutorOptions();
        if (_options.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be positive.");
        if (_options.QueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity must be positive.");

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queue = global::System.Threading.Channels.Channel.CreateBounded<LongRunningBusinessIoWorkItem>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = _options.MaxConcurrency == 1,
                SingleWriter = false,
            });
        _workers = Enumerable
            .Range(0, _options.MaxConcurrency)
            .Select(index => Task.Factory.StartNew(
                static state => ((LongRunningBusinessIoExecutor)state!).WorkerLoopAsync(),
                this,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap())
            .ToArray();
    }

    public async Task SubmitAsync(LongRunningBusinessIoWorkItem workItem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ThrowIfInvalid(workItem);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _queue.Writer.WriteAsync(workItem, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "Accepted long-running business IO work: owner={OwnerActorId}, operation={OperationName}, correlation={CorrelationId}, workItem={WorkItemId}",
            workItem.OwnerActorId,
            workItem.OperationName,
            workItem.CorrelationId,
            workItem.WorkItemId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var workItem in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                await ExecuteWorkItemAsync(workItem).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteWorkItemAsync(LongRunningBusinessIoWorkItem workItem)
    {
        var timeout = workItem.Timeout > TimeSpan.Zero
            ? workItem.Timeout
            : _options.DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(timeout);

        try
        {
            await workItem.ExecuteAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Long-running business IO work timed out: owner={OwnerActorId}, operation={OperationName}, correlation={CorrelationId}, workItem={WorkItemId}, timeout={Timeout}",
                workItem.OwnerActorId,
                workItem.OperationName,
                workItem.CorrelationId,
                workItem.WorkItemId,
                timeout);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Long-running business IO work failed without producing its typed completion: owner={OwnerActorId}, operation={OperationName}, correlation={CorrelationId}, workItem={WorkItemId}",
                workItem.OwnerActorId,
                workItem.OperationName,
                workItem.CorrelationId,
                workItem.WorkItemId);
        }
    }

    private static void ThrowIfInvalid(LongRunningBusinessIoWorkItem workItem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem.WorkItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem.OwnerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem.OperationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem.CorrelationId);
        ArgumentNullException.ThrowIfNull(workItem.ExecuteAsync);
    }
}
