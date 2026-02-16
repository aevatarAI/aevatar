// ─────────────────────────────────────────────────────────────
// EventRouter - event router for hierarchy and propagation logic.
// Routes events to Self / Up / Down / Both based on EventDirection.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Runtime.Routing;

/// <summary>Event router that tracks actor hierarchy and routes by direction.</summary>
public sealed class EventRouter
{
    private string? _parentId;
    private readonly HashSet<string> _childrenIds = [];

    /// <summary>Current actor ID.</summary>
    public string ActorId { get; }

    /// <summary>Creates router for the specified actor.</summary>
    /// <param name="actorId">Unique actor identifier.</param>
    public EventRouter(string actorId) => ActorId = actorId;

    /// <summary>Sets parent actor ID.</summary>
    /// <param name="parentId">Parent actor ID, or null when no parent exists.</param>
    public void SetParent(string? parentId) => _parentId = parentId;

    /// <summary>Clears parent actor reference.</summary>
    public void ClearParent() => _parentId = null;

    /// <summary>Parent actor ID, or null when no parent exists.</summary>
    public string? ParentId => _parentId;

    /// <summary>Adds child actor ID.</summary>
    /// <param name="childId">Child actor ID.</param>
    public void AddChild(string childId) => _childrenIds.Add(childId);

    /// <summary>Removes child actor ID.</summary>
    /// <param name="childId">Child actor ID to remove.</param>
    public void RemoveChild(string childId) => _childrenIds.Remove(childId);

    /// <summary>Read-only set of child actor IDs.</summary>
    public IReadOnlySet<string> ChildrenIds => _childrenIds;

    /// <summary>Routes event by direction: handle self first, then forward by Up/Down/Both.</summary>
    /// <param name="envelope">Event envelope.</param>
    /// <param name="handleSelf">Self-handling delegate.</param>
    /// <param name="sendToActor">Delegate that sends event to target actor.</param>
    public async Task RouteAsync(
        EventEnvelope envelope,
        Func<EventEnvelope, Task> handleSelf,
        Func<string, EventEnvelope, Task> sendToActor)
    {
        var publishers = GetPublishers(envelope);
        if (publishers.Contains(ActorId)) return;
        var updated = AddPublisher(envelope, ActorId);
        await handleSelf(updated);

        switch (envelope.Direction)
        {
            case EventDirection.Self: break;
            case EventDirection.Up:
                if (_parentId != null && !publishers.Contains(_parentId))
                    await sendToActor(_parentId, updated);
                break;
            case EventDirection.Down:
                foreach (var c in _childrenIds)
                    if (!publishers.Contains(c)) await sendToActor(c, updated);
                break;
            case EventDirection.Both:
                if (_parentId != null && !publishers.Contains(_parentId))
                    await sendToActor(_parentId, updated);
                foreach (var c in _childrenIds)
                    if (!publishers.Contains(c)) await sendToActor(c, updated);
                break;
        }
    }

    private const string PubKey = "__publishers";

    private static HashSet<string> GetPublishers(EventEnvelope e) =>
        e.Metadata.TryGetValue(PubKey, out var csv) && !string.IsNullOrEmpty(csv)
            ? [..csv.Split(',')] : [];

    private static EventEnvelope AddPublisher(EventEnvelope e, string id)
    {
        var cur = e.Metadata.GetValueOrDefault(PubKey, "");
        e.Metadata[PubKey] = string.IsNullOrEmpty(cur) ? id : $"{cur},{id}";
        return e;
    }
}
