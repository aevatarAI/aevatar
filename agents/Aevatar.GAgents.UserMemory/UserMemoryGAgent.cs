using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.UserMemory;

/// <summary>
/// Per-user memory actor that maintains a capped set of memory entries.
///
/// Actor ID: <c>user-memory-{userId}</c> (user-scoped).
///
/// Eviction policy (runs inside <see cref="TransitionState"/>):
///   1. When adding an entry that would exceed <see cref="MaxEntries"/>,
///      evict the oldest entry in the same category first.
///   2. If no same-category entry remains, evict the globally oldest entry.
///
/// After each state change, pushes the current state to the paired
/// <see cref="UserMemoryReadModelGAgent"/> via <c>SendToAsync</c>.
/// </summary>
public sealed class UserMemoryGAgent : GAgentBase<UserMemoryState>
{
    internal const int MaxEntries = 50;

    [EventHandler(EndpointName = "addMemoryEntry")]
    public async Task HandleMemoryEntryAdded(MemoryEntryAddedEvent evt)
    {
        if (evt.Entry is null
            || string.IsNullOrWhiteSpace(evt.Entry.Id)
            || string.IsNullOrWhiteSpace(evt.Entry.Content))
            return;

        // Idempotent: skip if an entry with this ID already exists
        if (State.Entries.Any(e => string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    [EventHandler(EndpointName = "removeMemoryEntry")]
    public async Task HandleMemoryEntryRemoved(MemoryEntryRemovedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.EntryId))
            return;

        // Idempotent: skip if not present
        if (!State.Entries.Any(e => string.Equals(e.Id, evt.EntryId, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    [EventHandler(EndpointName = "clearMemoryEntries")]
    public async Task HandleMemoryEntriesCleared(MemoryEntriesClearedEvent evt)
    {
        if (State.Entries.Count == 0)
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PushToReadModelAsync();
    }

    protected override UserMemoryState TransitionState(
        UserMemoryState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<MemoryEntryAddedEvent>(ApplyAdded)
            .On<MemoryEntryRemovedEvent>(ApplyRemoved)
            .On<MemoryEntriesClearedEvent>(ApplyCleared)
            .OrCurrent();
    }

    private static UserMemoryState ApplyAdded(
        UserMemoryState state, MemoryEntryAddedEvent evt)
    {
        var next = state.Clone();
        next.Entries.Add(evt.Entry.Clone());

        // Eviction: enforce global cap.
        // Priority: evict oldest in same category first, then globally oldest.
        while (next.Entries.Count > MaxEntries)
        {
            var category = evt.Entry.Category;
            var oldestSameCategory = next.Entries
                .Where(e => string.Equals(e.Category, category, StringComparison.Ordinal)
                            && !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                .OrderBy(e => e.CreatedAt)
                .FirstOrDefault();

            if (oldestSameCategory is not null)
            {
                next.Entries.Remove(oldestSameCategory);
            }
            else
            {
                var globallyOldest = next.Entries
                    .Where(e => !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                    .OrderBy(e => e.CreatedAt)
                    .FirstOrDefault();

                if (globallyOldest is not null)
                    next.Entries.Remove(globallyOldest);
                else
                    break;
            }
        }

        return next;
    }

    private static UserMemoryState ApplyRemoved(
        UserMemoryState state, MemoryEntryRemovedEvent evt)
    {
        var next = state.Clone();
        var entry = next.Entries.FirstOrDefault(e =>
            string.Equals(e.Id, evt.EntryId, StringComparison.Ordinal));

        if (entry is not null)
            next.Entries.Remove(entry);

        return next;
    }

    private static UserMemoryState ApplyCleared(
        UserMemoryState state, MemoryEntriesClearedEvent _)
    {
        var next = state.Clone();
        next.Entries.Clear();
        return next;
    }

    private async Task PushToReadModelAsync()
    {
        var readModelActorId = Id + "-readmodel";
        var runtime = Services.GetRequiredService<IActorRuntime>();
        if (await runtime.GetAsync(readModelActorId) is null)
            await runtime.CreateAsync<UserMemoryReadModelGAgent>(readModelActorId);
        var update = new UserMemoryReadModelUpdateEvent { Snapshot = State.Clone() };
        await SendToAsync(readModelActorId, update);
    }
}
