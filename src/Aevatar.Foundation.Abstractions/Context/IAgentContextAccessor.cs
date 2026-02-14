// ─────────────────────────────────────────────────────────────
// IAgentContextAccessor - agent context accessor contract.
// AsyncLocal-based context accessor supporting key/value propagation across agents.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Context;

/// <summary>
/// Agent execution context with key/value storage and propagation.
/// </summary>
public interface IAgentContext
{
    /// <summary>Gets the value for the specified key.</summary>
    T? Get<T>(string key);

    /// <summary>Sets the value for the specified key.</summary>
    void Set<T>(string key, T value);

    /// <summary>Removes the specified key.</summary>
    void Remove(string key);

    /// <summary>Gets all key/value pairs.</summary>
    IReadOnlyDictionary<string, object?> GetAll();
}

/// <summary>
/// AsyncLocal-based context accessor.
/// </summary>
public interface IAgentContextAccessor
{
    /// <summary>IAgentContext associated with the current async flow, or null.</summary>
    IAgentContext? Context { get; set; }
}