using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

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
/// </summary>
[GAgent("user.memory")]
public sealed class UserMemoryGAgent : GAgentBase<UserMemoryState>, IProjectedActor
{
    private static readonly long MaxUnixTimeMilliseconds =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static string ProjectionKind => "user-memory";

    internal const int MaxEntries = 50;

    [EventHandler(EndpointName = "addMemoryEntry")]
    public async Task HandleAddUserMemoryEntry(AddUserMemoryEntryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEntry(command.Entry);

        // Idempotent: skip if an entry with this ID already exists
        if (State.Entries.Any(e => string.Equals(e.Id, command.Entry.Id, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(new MemoryEntryAddedEvent
        {
            Entry = command.Entry.Clone(),
        });
    }

    [EventHandler(EndpointName = "removeMemoryEntry")]
    public async Task HandleRemoveUserMemoryEntry(RemoveUserMemoryEntryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCanonicalText(command.EntryId, "entry_id");

        // Idempotent: skip if not present
        if (!State.Entries.Any(e => string.Equals(e.Id, command.EntryId, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(new MemoryEntryRemovedEvent
        {
            EntryId = command.EntryId,
        });
    }

    [EventHandler(EndpointName = "clearMemoryEntries")]
    public async Task HandleClearUserMemoryEntries(ClearUserMemoryEntriesCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Entries.Count == 0)
            return;

        await PersistDomainEventAsync(new MemoryEntriesClearedEvent());
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
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
                .Where(e => e.Category == category
                            && !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                .OrderBy(e => e.CreatedAtMs)
                .FirstOrDefault();

            if (oldestSameCategory is not null)
            {
                next.Entries.Remove(oldestSameCategory);
            }
            else
            {
                var globallyOldest = next.Entries
                    .Where(e => !string.Equals(e.Id, evt.Entry.Id, StringComparison.Ordinal))
                    .OrderBy(e => e.CreatedAtMs)
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

    private static void ValidateEntry(UserMemoryEntryProto? entry)
    {
        if (entry is null)
            throw new InvalidOperationException("user_memory_entry_required");

        ValidateCanonicalText(entry.Id, "entry_id");
        ValidateCanonicalText(entry.Content, "content");
        if (!Enum.IsDefined(entry.Category) || entry.Category == UserMemoryCategory.Unspecified)
            throw new InvalidOperationException("user_memory_category_invalid");
        if (!Enum.IsDefined(entry.Source) || entry.Source == UserMemorySource.Unspecified)
            throw new InvalidOperationException("user_memory_source_invalid");
        if (entry.CreatedAtMs < 0 ||
            entry.UpdatedAtMs < entry.CreatedAtMs ||
            entry.UpdatedAtMs > MaxUnixTimeMilliseconds)
            throw new InvalidOperationException("user_memory_timestamp_invalid");
    }

    private static void ValidateCanonicalText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"user_memory_{field}_invalid");
    }

}
