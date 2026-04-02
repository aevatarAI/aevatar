using System.Collections.Concurrent;

namespace Aevatar.GAgents.StreamingProxy;

/// <summary>
/// Manages StreamingProxy room actors.
/// Phase 1: in-memory store. Phase 2: persistent storage.
/// </summary>
public sealed class StreamingProxyActorStore
{
    private readonly ConcurrentDictionary<string, List<RoomEntry>> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ParticipantEntry>> _participants = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<RoomEntry>> ListRoomsAsync(string scopeId, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        var rooms = _store.GetOrAdd(key, _ => []);
        IReadOnlyList<RoomEntry> result;
        lock (rooms)
        {
            result = rooms.ToList().AsReadOnly();
        }
        return Task.FromResult(result);
    }

    public Task<RoomEntry> CreateRoomAsync(string scopeId, string? roomName, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        var entry = new RoomEntry
        {
            RoomId = StreamingProxyDefaults.GenerateRoomId(),
            RoomName = roomName ?? "Group Chat",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var rooms = _store.GetOrAdd(key, _ => []);
        lock (rooms)
        {
            rooms.Add(entry);
        }
        return Task.FromResult(entry);
    }

    public Task<bool> DeleteRoomAsync(string scopeId, string roomId, CancellationToken ct = default)
    {
        var key = BuildKey(scopeId);
        if (!_store.TryGetValue(key, out var rooms))
            return Task.FromResult(false);

        bool removed;
        lock (rooms)
        {
            removed = rooms.RemoveAll(e =>
                string.Equals(e.RoomId, roomId, StringComparison.Ordinal)) > 0;
        }
        return Task.FromResult(removed);
    }

    public void AddParticipant(string scopeId, string roomId, string agentId, string displayName)
    {
        var key = BuildParticipantKey(scopeId, roomId);
        var participants = _participants.GetOrAdd(key, _ => []);
        lock (participants)
        {
            participants.RemoveAll(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal));
            participants.Add(new ParticipantEntry
            {
                AgentId = agentId,
                DisplayName = displayName,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    public IReadOnlyList<ParticipantEntry> ListParticipants(string scopeId, string roomId)
    {
        var key = BuildParticipantKey(scopeId, roomId);
        if (!_participants.TryGetValue(key, out var participants))
            return [];

        lock (participants)
        {
            return participants.ToList().AsReadOnly();
        }
    }

    private static string BuildKey(string scopeId) => scopeId.Trim().ToLowerInvariant();
    private static string BuildParticipantKey(string scopeId, string roomId) =>
        $"{scopeId.Trim().ToLowerInvariant()}:{roomId}";

    public sealed class RoomEntry
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class ParticipantEntry
    {
        public string AgentId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTimeOffset JoinedAt { get; set; }
    }
}
