using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorStateBindingAccessor
{
    IPersistentState<RuntimeActorGrainState>? Current { get; }

    IPersistentState<RuntimeActorCommittedStatePublicationGrainState>? CurrentCommittedStatePublication { get; }

    IDisposable Bind(
        IPersistentState<RuntimeActorGrainState> runtimeState,
        IPersistentState<RuntimeActorCommittedStatePublicationGrainState> committedStatePublication);
}
