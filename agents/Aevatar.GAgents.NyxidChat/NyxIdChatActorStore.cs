using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Manages NyxIdChat conversation actors.
/// Phase 1: in-memory store. Phase 2: chrono-storage persistence.
/// </summary>
public sealed class NyxIdChatActorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, List<ActorEntry>> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ActorEntry>> ListActorsAsync(string scopeId, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        var actors = _store.GetOrAdd(key, _ => []);
        IReadOnlyList<ActorEntry> result;
        lock (actors)
        {
            result = actors.ToList().AsReadOnly();
        }
        return Task.FromResult(result);
    }

    public Task<ActorEntry> CreateActorAsync(string scopeId, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        var entry = new ActorEntry
        {
            ActorId = NyxIdChatServiceDefaults.GenerateActorId(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var actors = _store.GetOrAdd(key, _ => []);
        lock (actors)
        {
            actors.Add(entry);
        }
        return Task.FromResult(entry);
    }

    public Task<bool> DeleteActorAsync(string scopeId, string actorId, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        if (!_store.TryGetValue(key, out var actors))
            return Task.FromResult(false);

        bool removed;
        lock (actors)
        {
            removed = actors.RemoveAll(e =>
                string.Equals(e.ActorId, actorId, StringComparison.Ordinal)) > 0;
        }
        return Task.FromResult(removed);
    }

    private static string BuildKey(string scopeId) => scopeId.Trim().ToLowerInvariant();

    public sealed class ActorEntry
    {
        public string ActorId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
