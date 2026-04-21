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
/// producer timeout <see cref="DefaultProducerTimeout"/>). If a producer can't enqueue within the
/// timeout, it throws so the stream observer propagates the exception and the persistent provider
/// re-delivers.
/// </para>
/// <para>
/// Delivery handler <see cref="OnNextAsync"/> completes <em>before</em> it returns, so a successful
/// return signals "pipeline fully awaited → safe to commit the stream offset". If the pipeline
/// throws, the exception propagates and triggers redelivery per Orleans persistent-stream semantics.
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
    private readonly System.Threading.Channels.Channel<ChatActivity> _buffer;
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
        _buffer = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(new BoundedChannelOptions(bufferCapacity)
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
    /// Delivery handler. Returns successfully when the activity has reached the pipeline without
    /// throwing; throws when the buffer is full past the configured timeout or the pipeline faults.
    /// </summary>
    public async Task OnNextAsync(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeoutCts.CancelAfter(_producerTimeout);

        try
        {
            await _buffer.Writer.WriteAsync(activity, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"DurableInboxSubscriber buffer full for {_producerTimeout.TotalMilliseconds}ms; triggering redelivery.",
                ex);
        }
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
            await foreach (var activity in _buffer.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var turnCtx = _contextFactory(activity);
                    await _pipeline.InvokeAsync(turnCtx, () => Task.CompletedTask, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Durable inbox worker failed for activity {ActivityId}; caller is expected to redeliver",
                        activity?.Id);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
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
        _cts.Dispose();
    }
}
