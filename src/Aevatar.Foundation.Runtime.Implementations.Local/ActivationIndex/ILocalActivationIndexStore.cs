using System.Collections.Concurrent;

namespace Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;

internal interface ILocalActivationIndexStore
{
    Task UpsertAsync(string actorId, string agentTypeName, CancellationToken ct = default);

    Task<string?> GetAgentTypeNameAsync(string actorId, CancellationToken ct = default);

    Task DeleteAsync(string actorId, CancellationToken ct = default);
}

internal sealed class InMemoryLocalActivationIndexStore : ILocalActivationIndexStore
{
    private readonly ConcurrentDictionary<string, string> _index = new(StringComparer.Ordinal);

    public Task UpsertAsync(string actorId, string agentTypeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentTypeName);
        ct.ThrowIfCancellationRequested();
        _index[actorId] = agentTypeName;
        return Task.CompletedTask;
    }

    public Task<string?> GetAgentTypeNameAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_index.GetValueOrDefault(actorId));
    }

    public Task DeleteAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        _index.TryRemove(actorId, out _);
        return Task.CompletedTask;
    }
}
