using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Context;

internal static class OrleansAgentContextRequestContext
{
    // Orleans RequestContext uses reserved internal headers with leading "__".
    // Use a dedicated non-reserved prefix for RPC propagation keys.
    internal const string RequestContextPrefix = "aevatarac_";
    private static readonly string Prefix = RequestContextPrefix;

    public static bool HasAnyContextKeys() => EnumerateContextKeys().Any();

    public static IReadOnlyDictionary<string, object?> SnapshotContextValues()
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in EnumerateContextKeys())
            snapshot[key] = RequestContext.Get(key);
        return snapshot;
    }

    public static void RestoreContextValues(IReadOnlyDictionary<string, object?> snapshot)
    {
        ClearContextKeys();
        foreach (var (key, value) in snapshot)
        {
            if (value == null)
            {
                RequestContext.Remove(key);
                continue;
            }

            RequestContext.Set(key, value);
        }
    }

    public static void ClearContextKeys()
    {
        foreach (var key in EnumerateContextKeys().ToArray())
            RequestContext.Remove(key);
    }

    public static void ReplaceFromContext(IAgentContext context)
    {
        ClearContextKeys();
        UpsertFromContext(context);
    }

    public static void UpsertFromContext(IAgentContext context)
    {
        foreach (var (key, value) in context.GetAll())
        {
            if (value == null)
                continue;

            RequestContext.Set($"{Prefix}{key}", value.ToString() ?? string.Empty);
        }
    }

    private static IEnumerable<string> EnumerateContextKeys()
    {
        foreach (var key in RequestContext.Keys)
        {
            if (key.StartsWith(Prefix, StringComparison.Ordinal))
                yield return key;
        }
    }
}

internal sealed class RequestContextAgentContext : IAgentContext
{
    private static readonly string Prefix = OrleansAgentContextRequestContext.RequestContextPrefix;

    public T? Get<T>(string key)
    {
        var value = RequestContext.Get($"{Prefix}{key}");
        return value is T typed ? typed : default;
    }

    public void Set<T>(string key, T value)
    {
        if (value == null)
        {
            Remove(key);
            return;
        }

        RequestContext.Set($"{Prefix}{key}", value.ToString() ?? string.Empty);
    }

    public void Remove(string key) =>
        RequestContext.Remove($"{Prefix}{key}");

    public IReadOnlyDictionary<string, object?> GetAll()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in OrleansAgentContextRequestContext.SnapshotContextValues())
            values[key[Prefix.Length..]] = value;
        return values;
    }
}

public sealed class OrleansAgentContextAccessor : IAgentContextAccessor
{
    private static readonly IAgentContext Proxy = new RequestContextAgentContext();

    public IAgentContext? Context
    {
        get => OrleansAgentContextRequestContext.HasAnyContextKeys() ? Proxy : null;
        set
        {
            if (value == null)
            {
                OrleansAgentContextRequestContext.ClearContextKeys();
                return;
            }

            OrleansAgentContextRequestContext.ReplaceFromContext(value);
        }
    }
}
