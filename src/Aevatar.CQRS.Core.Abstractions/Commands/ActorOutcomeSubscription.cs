namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed class ActorOutcomeSubscription<TOutcome> : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;

    public ActorOutcomeSubscription(
        Task<TOutcome> outcome,
        Func<ValueTask> disposeAsync)
    {
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        _disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
    }

    public Task<TOutcome> Outcome { get; }

    public ValueTask DisposeAsync() => _disposeAsync();
}
