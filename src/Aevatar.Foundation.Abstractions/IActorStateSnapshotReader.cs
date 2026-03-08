using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

public interface IActorStateSnapshotReader
{
    Task<TState?> GetStateAsync<TState>(string actorId, CancellationToken ct = default)
        where TState : class, IMessage, new();
}
