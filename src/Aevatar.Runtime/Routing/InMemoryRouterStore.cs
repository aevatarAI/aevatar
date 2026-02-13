// ─────────────────────────────────────────────────────────────
// InMemoryRouterStore - in-memory router hierarchy store.
// Contains RouterHierarchy, IRouterHierarchyStore, and in-memory implementation.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace Aevatar.Routing;

/// <summary>Router hierarchy snapshot: parent ID and child ID set.</summary>
/// <param name="ParentId">Parent actor ID, or null when no parent exists.</param>
/// <param name="ChildrenIds">Child actor ID set.</param>
public sealed record RouterHierarchy(string? ParentId, IReadOnlySet<string> ChildrenIds);

/// <summary>Router hierarchy persistence contract.</summary>
public interface IRouterHierarchyStore
{
    /// <summary>Loads router hierarchy for the specified actor.</summary>
    Task<RouterHierarchy?> LoadAsync(string actorId, CancellationToken ct = default);

    /// <summary>Saves router hierarchy for the specified actor.</summary>
    Task SaveAsync(string actorId, RouterHierarchy hierarchy, CancellationToken ct = default);

    /// <summary>Deletes router hierarchy for the specified actor.</summary>
    Task DeleteAsync(string actorId, CancellationToken ct = default);
}

/// <summary>In-memory router hierarchy store implementation.</summary>
public sealed class InMemoryRouterStore : IRouterHierarchyStore
{
    private readonly ConcurrentDictionary<string, RouterHierarchy> _store = new();

    /// <summary>Loads router hierarchy from memory.</summary>
    public Task<RouterHierarchy?> LoadAsync(string actorId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(actorId));

    /// <summary>Saves router hierarchy into memory.</summary>
    public Task SaveAsync(string actorId, RouterHierarchy hierarchy, CancellationToken ct = default)
    { _store[actorId] = hierarchy; return Task.CompletedTask; }

    /// <summary>Deletes router hierarchy from memory.</summary>
    public Task DeleteAsync(string actorId, CancellationToken ct = default)
    { _store.TryRemove(actorId, out _); return Task.CompletedTask; }
}
