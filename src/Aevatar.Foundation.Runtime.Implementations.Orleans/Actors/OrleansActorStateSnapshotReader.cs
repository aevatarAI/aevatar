using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActorStateSnapshotReader : IActorStateSnapshotReader
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorStateSnapshotReader(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<TState?> GetStateAsync<TState>(string actorId, CancellationToken ct = default)
        where TState : class, IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var snapshot = await _grainFactory
            .GetGrain<IRuntimeActorGrain>(actorId)
            .GetStateSnapshotAsync();

        if (snapshot?.StateBytes == null || string.IsNullOrWhiteSpace(snapshot.StateTypeName))
            return null;

        var expectedFullName = typeof(TState).FullName ?? typeof(TState).Name;
        var expectedAssemblyQualifiedName = typeof(TState).AssemblyQualifiedName;
        if (!string.Equals(snapshot.StateTypeName, expectedFullName, StringComparison.Ordinal) &&
            !string.Equals(snapshot.StateTypeName, expectedAssemblyQualifiedName, StringComparison.Ordinal))
            return null;

        var state = new TState();
        state.MergeFrom(snapshot.StateBytes);
        return state;
    }
}
