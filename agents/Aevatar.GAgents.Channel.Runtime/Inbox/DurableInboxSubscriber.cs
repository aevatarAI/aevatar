using System.Threading.Channels;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Bridges a durable-inbox <see cref="ChatActivity"/> feed into the channel pipeline with the
/// return→commit / throw→redeliver observer semantics described by RFC §5.8 / §9.5.2.
/// </summary>
/// <remarks>
/// <para>
/// The subscriber owns one bounded <see cref="System.Threading.Channels.Channel{T}"/> working
/// buffer (default capacity <see cref="DefaultBufferCapacity"/>, <see cref="BoundedChannelFullMode.Wait"/>,
/// producer timeout <see cref="DefaultProducerTimeout"/>). If a producer cannot enqueue within the
/// timeout, it throws so the stream observer propagates the exception and the persistent provider
/// re-delivers.
/// </para>
/// <para>
/// Each enqueued activity carries its own <see cref="TaskCompletionSource"/>; the worker loop awaits
/// <see cref="ChannelPipeline.InvokeAsync"/> and then signals the completion source with either the
/// success or the pipeline exception. <see cref="OnNextAsync"/> only returns after that completion
/// source signals, so a successful return guarantees the pipeline really finished and the stream
/// provider may commit. A pipeline fault propagates back through the completion source so the
/// observer throws and the provider re-delivers. This preserves the <c>return → commit,
/// throw → redeliver</c> contract even under a buffered worker.
/// </para>
/// </remarks>
public sealed class DurableInboxSubscriber : IAsyncDisposable
{
    /// <summary>Default working buffer capacity per RFC §5.8.</summary>
    public const int DefaultBufferCapacity = 1000;

    /// <summary>Default producer timeout per RFC §5.8 (500 ms).</summary>
    public static readonly TimeSpan DefaultProducerTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ChannelPipeline _pipeline;
    private readonly IServiceProvider _services;
    private readonly Func<ChatActivity, ITurnContext> _contextFactory;
    private readonly ILogger<DurableInboxSubscriber> _logger;
    private readonly System.Threading.Channels.Channel<PendingItem> _buffer;
    private readonly TimeSpan _producerTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    /// <summary>
    /// Creates one subscriber. Use <see cref="OnNextAsync"/> as the delivery handler passed to
    /// <see cref="Aevatar.Foundation.Abstractions.IStream.SubscribeAsync{T}"/>.
    /// </summary>
    public DurableInboxSubscriber(
        ChannelPipeline pipeline,
        IServiceProvider services,
        Func<ChatActivity, ITurnContext> contextFactory,
        ILogger<DurableInboxSubscriber> logger,
        int bufferCapacity = DefaultBufferCapacity,
        TimeSpan? producerTimeout = null)
    {
        _pipeline = pipeline;
        _services = services;
        _contextFactory = contextFactory;
        _logger = logger;
        _producerTimeout = producerTimeout ?? DefaultProducerTimeout;
        _buffer = System.Threading.Channels.Channel.CreateBounded<PendingItem>(new BoundedChannelOptions(bufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Starts the worker loop that drains the bounded buffer into the pipeline.
    /// </summary>
    public void Start()
    {
        if (_worker is not null)
            return;

        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Delivery handler. Only returns after the pipeline has fully processed the activity, so a
    /// successful return signals "safe to commit" and a throw signals "redeliver". Bounded-buffer
    /// saturation at enqueue time throws <see cref="TimeoutException"/> for the same redelivery path.
    /// </summary>
    public async Task OnNextAsync(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (_cts.IsCancellationRequested)
            throw new InvalidOperationException("DurableInboxSubscriber is disposed.");

        var pending = new PendingItem(activity);
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            timeoutCts.CancelAfter(_producerTimeout);
            try
            {
                await _buffer.Writer.WriteAsync(pending, timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"DurableInboxSubscriber buffer full for {_producerTimeout.TotalMilliseconds}ms; triggering redelivery.",
                    ex);
            }
        }

        // Block until the worker has actually finished (successful → commit; throw → redeliver).
        await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous delivery handler that forwards directly to the pipeline without buffering.
    /// Useful for tests and for adapters that guarantee back-pressure upstream.
    /// </summary>
    public Task OnNextInlineAsync(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var turnCtx = _contextFactory(activity);
        return _pipeline.InvokeAsync(turnCtx, () => Task.CompletedTask, _cts.Token);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pending in _buffer.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var turnCtx = _contextFactory(pending.Activity);
                    await _pipeline.InvokeAsync(turnCtx, () => Task.CompletedTask, ct);
                    pending.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled(ct);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Durable inbox worker failed for activity {ActivityId}; propagating redelivery",
                        pending.Activity?.Id);
                    pending.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — any still-waiting OnNextAsync callers get cancelled via DrainPending.
        }
        finally
        {
            DrainPending();
        }
    }

    private void DrainPending()
    {
        // Signal any observers still waiting on enqueued-but-not-processed items so they don't hang.
        while (_buffer.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetException(
                new InvalidOperationException("DurableInboxSubscriber stopped before activity was processed; redelivery expected."));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _buffer.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "DurableInboxSubscriber worker terminated with exception during dispose");
            }
        }
        DrainPending();
        _cts.Dispose();
    }

    private sealed class PendingItem
    {
        public PendingItem(ChatActivity activity)
        {
            Activity = activity;
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ChatActivity Activity { get; }

        public TaskCompletionSource Completion { get; }
    }
}
