using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class AsyncLocalRuntimeActorStateBindingAccessor : IRuntimeActorStateBindingAccessor
{
    private static readonly AsyncLocal<IPersistentState<RuntimeActorGrainState>?> CurrentState = new();

    public IPersistentState<RuntimeActorGrainState>? Current => CurrentState.Value;

    public IDisposable Bind(IPersistentState<RuntimeActorGrainState> runtimeState)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);

        var previous = CurrentState.Value;
        CurrentState.Value = runtimeState;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly IPersistentState<RuntimeActorGrainState>? _previous;
        private bool _disposed;

        public RestoreScope(IPersistentState<RuntimeActorGrainState>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentState.Value = _previous;
            _disposed = true;
        }
    }
}
