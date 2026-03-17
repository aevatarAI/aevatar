using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionTimedEventOutputStream
    : IEventOutputStream<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent>
{
    private readonly TimeSpan _decisionTimeout;

    public ScriptEvolutionTimedEventOutputStream(ScriptingInteractionTimeoutOptions timeoutOptions)
    {
        _decisionTimeout = (timeoutOptions ?? throw new ArgumentNullException(nameof(timeoutOptions)))
            .ResolveEvolutionCompletionTimeout();
    }

    public async Task PumpAsync(
        IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> events,
        Func<ScriptEvolutionSessionCompletedEvent, CancellationToken, ValueTask> emitAsync,
        Func<ScriptEvolutionSessionCompletedEvent, bool>? shouldStop = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emitAsync);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_decisionTimeout);

        try
        {
            await foreach (var evt in events.WithCancellation(timeoutCts.Token))
            {
                await emitAsync(evt, ct);

                if (shouldStop?.Invoke(evt) == true)
                    return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return;
        }
    }
}
