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

/// <summary>Injects AgentContext into EventEnvelope metadata and extracts it back.</summary>
public static class AgentContextPropagator
{
    private const string Prefix = "__ctx_";

    /// <summary>Writes current context into envelope metadata.</summary>
    public static void Inject(IAgentContext? context, EventEnvelope envelope)
    {
        if (context == null) return;
        foreach (var (key, value) in context.GetAll())
            if (value != null)
                envelope.Metadata[$"{Prefix}{key}"] = value.ToString() ?? "";
    }

    /// <summary>Restores context from envelope metadata.</summary>
    public static IAgentContext Extract(EventEnvelope envelope)
    {
        var ctx = new AsyncLocalAgentContext();
        foreach (var (key, value) in envelope.Metadata)
            if (key.StartsWith(Prefix))
                ctx.Set(key[Prefix.Length..], value);
        return ctx;
    }
}
