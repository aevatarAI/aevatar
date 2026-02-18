using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Core.Streaming;

public sealed class DefaultEventOutputStream<TEvent, TFrame> : IEventOutputStream<TEvent, TFrame>
{
    private readonly IEventFrameMapper<TEvent, TFrame> _mapper;

    public DefaultEventOutputStream(IEventFrameMapper<TEvent, TFrame> mapper)
    {
        _mapper = mapper;
    }

    public async Task PumpAsync(
        IAsyncEnumerable<TEvent> events,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TEvent, bool>? shouldStop = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emitAsync);

        await foreach (var evt in events.WithCancellation(ct))
        {
            var frame = _mapper.Map(evt);
            await emitAsync(frame, ct);

            if (shouldStop?.Invoke(evt) == true)
                break;
        }
    }
}
