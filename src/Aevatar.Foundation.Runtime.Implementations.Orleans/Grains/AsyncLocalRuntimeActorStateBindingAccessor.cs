using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class AsyncLocalRuntimeActorStateBindingAccessor : IRuntimeActorStateBindingAccessor
{
    private static readonly AsyncLocal<RuntimeActorStateBinding?> CurrentBinding = new();

    public IPersistentState<RuntimeActorGrainState>? Current => CurrentBinding.Value?.RuntimeState;

    public IPersistentState<RuntimeActorCommittedStatePublicationGrainState>?
        CurrentCommittedStatePublication => CurrentBinding.Value?.CommittedStatePublication;

    public IDisposable Bind(
        IPersistentState<RuntimeActorGrainState> runtimeState,
        IPersistentState<RuntimeActorCommittedStatePublicationGrainState> committedStatePublication)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(committedStatePublication);

        var previous = CurrentBinding.Value;
        CurrentBinding.Value = new RuntimeActorStateBinding(runtimeState, committedStatePublication);
        return new RestoreScope(previous);
    }

    private sealed record RuntimeActorStateBinding(
        IPersistentState<RuntimeActorGrainState> RuntimeState,
        IPersistentState<RuntimeActorCommittedStatePublicationGrainState> CommittedStatePublication);

    private sealed class RestoreScope : IDisposable
    {
        private readonly RuntimeActorStateBinding? _previous;
        private bool _disposed;

        public RestoreScope(RuntimeActorStateBinding? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentBinding.Value = _previous;
            _disposed = true;
        }
    }
}
