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
///   1. Without a retention policy, preserve the legacy same-category-first behavior.
///   2. With a policy, enforce the added category's cap before the global cap.
///   3. At the global cap, higher-ranked categories are evicted first and rank ties
///      retain the legacy same-category-first order.
///
/// </summary>
[GAgent("user.memory")]
public sealed class UserMemoryGAgent : GAgentBase<UserMemoryState>, IProjectedActor
{
    private static readonly long MaxUnixTimeMilliseconds =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static string ProjectionKind => "user-memory";

    internal const int MaxEntries = 50;
    private const int DefaultEvictionRank = 100;
    private const int MaxEvictionRank = 1000;

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

    [EventHandler(EndpointName = "replaceRetentionPolicy")]
    public async Task HandleReplaceRetentionPolicy(ReplaceUserMemoryRetentionPolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedStateVersion < 0)
            throw new InvalidOperationException("user_memory_expected_state_version_invalid");

        var mutationId = NormalizeMutationId(command.MutationId);
        var replacedEvent = BuildRetentionPolicyReplacedEvent(State, command, mutationId);
        if (string.Equals(State.LastRetentionPolicyMutationId, mutationId, StringComparison.Ordinal))
        {
            if (PolicyMatchesState(State.RetentionPolicy, replacedEvent.Policy))
                return;

            throw new InvalidOperationException("user_memory_policy_mutation_conflict");
        }

        var currentVersion = EventSourcing?.CurrentVersion
            ?? throw new InvalidOperationException("user_memory_event_sourcing_unavailable");
        if (command.ExpectedStateVersion != currentVersion)
            throw new InvalidOperationException("user_memory_expected_state_version_conflict");

        await PersistDomainEventAsync(replacedEvent);
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
            .On<UserMemoryRetentionPolicyReplacedEvent>(ApplyRetentionPolicyReplaced)
            .OrCurrent();
    }

    // Implement (issue #3528):
    //   Behavior: Policy replacement is validated and revisioned inside the owning actor state.
    //   Why this shape: Replay must derive retention from committed facts without external policy reads.
    public static UserMemoryRetentionPolicyReplacedEvent BuildRetentionPolicyReplacedEvent(
        UserMemoryState state,
        ReplaceUserMemoryRetentionPolicyCommand command,
        string? normalizedMutationId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var policy = new UserMemoryRetentionPolicy
        {
            PolicyRevision = (state.RetentionPolicy?.PolicyRevision ?? 0) + 1,
        };
        policy.Rules.AddRange(NormalizeRules(command.Rules));
        return new UserMemoryRetentionPolicyReplacedEvent
        {
            Policy = policy,
            MutationId = normalizedMutationId ?? NormalizeMutationId(command.MutationId),
        };
    }

    private static UserMemoryState ApplyAdded(
        UserMemoryState state, MemoryEntryAddedEvent evt)
    {
        var next = state.Clone();
        next.Entries.Add(evt.Entry.Clone());

        if (next.RetentionPolicy is not null)
            EnforceCategoryCap(next, evt.Entry);

        while (next.Entries.Count > MaxEntries)
        {
            var evicted = next.RetentionPolicy is null
                ? SelectLegacyEvictionCandidate(next.Entries, evt.Entry)
                : SelectPolicyEvictionCandidate(next, evt.Entry);
            if (evicted is null)
                break;

            next.Entries.Remove(evicted);
        }

        return next;
    }

    private static void EnforceCategoryCap(UserMemoryState state, UserMemoryEntryProto addedEntry)
    {
        var rule = state.RetentionPolicy!.Rules.FirstOrDefault(candidate =>
            candidate.Category == addedEntry.Category);
        if (rule is null || rule.MaxEntries == 0)
            return;

        while (state.Entries.Count(entry => entry.Category == addedEntry.Category) > rule.MaxEntries)
        {
            var oldest = state.Entries
                .Where(entry => entry.Category == addedEntry.Category &&
                    !string.Equals(entry.Id, addedEntry.Id, StringComparison.Ordinal))
                .OrderBy(entry => entry.CreatedAtMs)
                .FirstOrDefault();
            if (oldest is null)
                break;

            state.Entries.Remove(oldest);
        }
    }

    // Implement (issue #3528):
    //   Behavior: Global eviction ranks categories, then preserves legacy ordering for equal ranks.
    //   Why this shape: It protects low-rank categories without changing tie behavior or evicting the new entry.
    private static UserMemoryEntryProto? SelectPolicyEvictionCandidate(
        UserMemoryState state,
        UserMemoryEntryProto addedEntry)
    {
        var candidates = state.Entries
            .Where(entry => !string.Equals(entry.Id, addedEntry.Id, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var highestRank = candidates.Max(entry => ResolveEvictionRank(state.RetentionPolicy!, entry.Category));
        var highestRankCandidates = candidates
            .Where(entry => ResolveEvictionRank(state.RetentionPolicy!, entry.Category) == highestRank)
            .ToArray();
        return SelectLegacyEvictionCandidate(highestRankCandidates, addedEntry);
    }

    private static UserMemoryEntryProto? SelectLegacyEvictionCandidate(
        IEnumerable<UserMemoryEntryProto> entries,
        UserMemoryEntryProto addedEntry) =>
        entries
            .Where(entry => entry.Category == addedEntry.Category &&
                !string.Equals(entry.Id, addedEntry.Id, StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAtMs)
            .FirstOrDefault()
        ?? entries
            .Where(entry => !string.Equals(entry.Id, addedEntry.Id, StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAtMs)
            .FirstOrDefault();

    private static int ResolveEvictionRank(
        UserMemoryRetentionPolicy policy,
        UserMemoryCategory category) =>
        policy.Rules.FirstOrDefault(rule => rule.Category == category)?.EvictionRank
        ?? DefaultEvictionRank;

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

    private static UserMemoryState ApplyRetentionPolicyReplaced(
        UserMemoryState state,
        UserMemoryRetentionPolicyReplacedEvent evt)
    {
        var next = state.Clone();
        next.RetentionPolicy = evt.Policy.Clone();
        next.LastRetentionPolicyMutationId = evt.MutationId;
        return next;
    }

    private static IReadOnlyList<UserMemoryCategoryRetentionRule> NormalizeRules(
        IEnumerable<UserMemoryCategoryRetentionRule> rules)
    {
        var normalized = new List<UserMemoryCategoryRetentionRule>();
        var categories = new HashSet<UserMemoryCategory>();
        foreach (var rule in rules ?? [])
        {
            if (!Enum.IsDefined(rule.Category) || rule.Category == UserMemoryCategory.Unspecified)
                throw new InvalidOperationException("user_memory_policy_category_invalid");
            if (!categories.Add(rule.Category))
                throw new InvalidOperationException("user_memory_policy_category_duplicate");
            if (rule.MaxEntries is < 0 or > MaxEntries)
                throw new InvalidOperationException("user_memory_policy_max_entries_invalid");
            if (rule.EvictionRank is < 0 or > MaxEvictionRank)
                throw new InvalidOperationException("user_memory_policy_eviction_rank_invalid");

            normalized.Add(rule.Clone());
        }

        return normalized.OrderBy(static rule => rule.Category).ToArray();
    }

    private static bool PolicyMatchesState(
        UserMemoryRetentionPolicy? statePolicy,
        UserMemoryRetentionPolicy policy) =>
        statePolicy is not null && statePolicy.Rules.SequenceEqual(policy.Rules);

    private static string NormalizeMutationId(string? mutationId)
    {
        var normalized = mutationId?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException("user_memory_policy_mutation_id_invalid");
        return normalized;
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
