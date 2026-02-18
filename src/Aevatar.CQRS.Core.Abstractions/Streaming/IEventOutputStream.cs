namespace Aevatar.CQRS.Core.Abstractions.Streaming;

public interface IEventOutputStream<in TEvent, TFrame>
{
    Task PumpAsync(
        IAsyncEnumerable<TEvent> events,
        string executionId,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);
}
