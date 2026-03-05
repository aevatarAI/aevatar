namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class AsyncLocalRuntimeActorInlineCallbackSchedulerBindingAccessor
    : IRuntimeActorInlineCallbackSchedulerBindingAccessor
{
    private static readonly AsyncLocal<IRuntimeActorInlineCallbackScheduler?> CurrentScheduler = new();

    public IRuntimeActorInlineCallbackScheduler? Current => CurrentScheduler.Value;

    public IDisposable Bind(IRuntimeActorInlineCallbackScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        var previous = CurrentScheduler.Value;
        CurrentScheduler.Value = scheduler;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly IRuntimeActorInlineCallbackScheduler? _previous;
        private bool _disposed;

        public RestoreScope(IRuntimeActorInlineCallbackScheduler? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentScheduler.Value = _previous;
            _disposed = true;
        }
    }
}
