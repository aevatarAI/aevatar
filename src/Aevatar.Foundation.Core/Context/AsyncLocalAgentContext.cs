using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Context;

namespace Aevatar.Foundation.Core;

/// <summary>AsyncLocal-based agent execution context.</summary>
public sealed class AsyncLocalAgentContext : IAgentContext
{
    private readonly ConcurrentDictionary<string, object?> _data = new();

    public T? Get<T>(string key) =>
        _data.TryGetValue(key, out var val) && val is T typed ? typed : default;

    public void Set<T>(string key, T value) => _data[key] = value;
    public void Remove(string key) => _data.TryRemove(key, out _);

    public IReadOnlyDictionary<string, object?> GetAll() =>
        new Dictionary<string, object?>(_data);
}

/// <summary>AsyncLocal-based IAgentContextAccessor implementation.</summary>
public sealed class AsyncLocalAgentContextAccessor : IAgentContextAccessor
{
    private static readonly AsyncLocal<IAgentContext?> Store = new();
    public IAgentContext? Context { get => Store.Value; set => Store.Value = value; }
}
