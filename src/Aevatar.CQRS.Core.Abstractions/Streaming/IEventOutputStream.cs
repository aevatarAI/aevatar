namespace Aevatar.CQRS.Core.Abstractions.Streaming;

public interface IEventOutputStream<TEvent, TFrame>
{
    Task PumpAsync(
        IAsyncEnumerable<TEvent> events,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TEvent, bool>? shouldStop = null,
        CancellationToken ct = default);
}
