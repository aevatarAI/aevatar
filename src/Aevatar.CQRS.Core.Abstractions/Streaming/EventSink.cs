using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.CQRS.Core.Abstractions.Streaming;

public interface IEventSink<TEvent> : IAsyncDisposable
{
    void Push(TEvent evt);

    ValueTask PushAsync(TEvent evt, CancellationToken ct = default);

    void Complete();

    IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken ct = default);
}

public sealed class EventSinkBackpressureException : InvalidOperationException
{
    public EventSinkBackpressureException()
        : base("Event sink channel is full.")
    {
    }
}

public sealed class EventSinkCompletedException : InvalidOperationException
{
    public EventSinkCompletedException()
        : base("Event sink channel is completed.")
    {
    }
}

public sealed class EventChannel<TEvent> : IEventSink<TEvent>
{
    private readonly Channel<TEvent> _channel;
    private readonly BoundedChannelFullMode _fullMode;

    public EventChannel(
        int capacity = 1024,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        var resolvedCapacity = capacity > 0 ? capacity : 1024;
        _fullMode = fullMode;
        _channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(resolvedCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Push(TEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public async ValueTask PushAsync(TEvent evt, CancellationToken ct = default)
    {
        if (_fullMode == BoundedChannelFullMode.Wait)
        {
            try
            {
                await _channel.Writer.WriteAsync(evt, ct);
            }
            catch (ChannelClosedException)
            {
                throw new EventSinkCompletedException();
            }

            return;
        }

        if (!_channel.Writer.TryWrite(evt))
            throw ResolveWriteFailureException();
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<TEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private Exception ResolveWriteFailureException()
    {
        return _channel.Reader.Completion.IsCompleted
            ? new EventSinkCompletedException()
            : new EventSinkBackpressureException();
    }
}
