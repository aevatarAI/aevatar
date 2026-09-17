using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Actors;

public sealed class AsyncLocalRuntimeActorStateSchemaContextAccessor
    : IRuntimeActorStateSchemaContextReader,
      IRuntimeActorStateSchemaContextAccessor,
      IRuntimeActorStateSchemaContextBinder
{
    private static readonly AsyncLocal<RuntimeActorStateSchemaContext?> CurrentContext = new();

    public RuntimeActorStateSchemaContext? Current => CurrentContext.Value;

    public IDisposable Bind(RuntimeActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var previous = CurrentContext.Value;
        CurrentContext.Value = RuntimeActorStateSchemaContext.FromIdentity(identity);
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(RuntimeActorStateSchemaContext? previous) : IDisposable
    {
        private RuntimeActorStateSchemaContext? _previous = previous;

        public void Dispose()
        {
            CurrentContext.Value = _previous;
            _previous = null;
        }
    }
}
