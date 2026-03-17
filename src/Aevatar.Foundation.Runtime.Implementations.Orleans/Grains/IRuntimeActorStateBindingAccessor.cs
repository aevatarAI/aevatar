using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorStateBindingAccessor
{
    IPersistentState<RuntimeActorGrainState>? Current { get; }

    IDisposable Bind(IPersistentState<RuntimeActorGrainState> runtimeState);
}
