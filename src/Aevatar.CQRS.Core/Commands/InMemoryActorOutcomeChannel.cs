using System.Collections.Concurrent;
using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class InMemoryActorOutcomeChannel<TOutcome> : IActorOutcomeChannel<TOutcome>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TOutcome>> _pending = new(StringComparer.Ordinal);

    public Task<ActorOutcomeSubscription<TOutcome>> SubscribeAsync(
        string commandId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ct.ThrowIfCancellationRequested();

        var source = _pending.GetOrAdd(
            commandId,
            static _ => new TaskCompletionSource<TOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));

        return Task.FromResult(new ActorOutcomeSubscription<TOutcome>(
            source.Task,
            () =>
            {
                _pending.TryRemove(commandId, out _);
                return ValueTask.CompletedTask;
            }));
    }

    public Task PublishAsync(
        string commandId,
        TOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(outcome);
        ct.ThrowIfCancellationRequested();

        var source = _pending.GetOrAdd(
            commandId,
            static _ => new TaskCompletionSource<TOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
        source.TrySetResult(outcome);
        return Task.CompletedTask;
    }
}
