using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class AgentRegistryGAgent : GAgentBase<AgentRegistryState>
{
    public const string WellKnownId = "agent-registry-store";

    protected override AgentRegistryState TransitionState(AgentRegistryState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentRegistryUpsertedEvent>(ApplyUpserted)
            .On<AgentRegistryExecutionUpdatedEvent>(ApplyExecutionUpdated)
            .On<AgentRegistryTombstonedEvent>(ApplyTombstoned)
            .On<AgentRegistryTombstonesCompactedEvent>(ApplyTombstonesCompacted)
            .OrCurrent();

    [EventHandler]
    public async Task HandleUpsertAsync(AgentRegistryUpsertCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot upsert agent registry entry with empty agent id");
            return;
        }

        var existing = State.Entries.FirstOrDefault(x => string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal));
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var entry = new AgentRegistryEntry
        {
            AgentId = command.AgentId.Trim(),
            Platform = MergeNonEmpty(command.Platform, existing?.Platform),
            ConversationId = MergeNonEmpty(command.ConversationId, existing?.ConversationId),
            NyxProviderSlug = MergeNonEmpty(command.NyxProviderSlug, existing?.NyxProviderSlug),
            NyxApiKey = MergeNonEmpty(command.NyxApiKey, existing?.NyxApiKey),
            OwnerNyxUserId = MergeNonEmpty(command.OwnerNyxUserId, existing?.OwnerNyxUserId),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Tombstoned = false,
            AgentType = MergeNonEmpty(command.AgentType, existing?.AgentType),
            TemplateName = MergeNonEmpty(command.TemplateName, existing?.TemplateName),
            ScopeId = MergeNonEmpty(command.ScopeId, existing?.ScopeId),
            ApiKeyId = MergeNonEmpty(command.ApiKeyId, existing?.ApiKeyId),
            ScheduleCron = MergeNonEmpty(command.ScheduleCron, existing?.ScheduleCron),
            ScheduleTimezone = MergeNonEmpty(command.ScheduleTimezone, existing?.ScheduleTimezone),
            Status = MergeNonEmpty(command.Status, existing?.Status),
            LastRunAt = existing?.LastRunAt,
            NextRunAt = existing?.NextRunAt,
            ErrorCount = existing?.ErrorCount ?? 0,
            LastError = existing?.LastError ?? string.Empty,
        };

        await PersistDomainEventAsync(new AgentRegistryUpsertedEvent
        {
            Entry = entry,
        });
    }

    [EventHandler]
    public async Task HandleTombstoneAsync(AgentRegistryTombstoneCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot tombstone agent registry entry with empty agent id");
            return;
        }

        if (State.Entries.All(x => !string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal)))
        {
            Logger.LogWarning("Cannot tombstone missing agent registry entry: {AgentId}", command.AgentId);
            return;
        }

        await PersistDomainEventAsync(new AgentRegistryTombstonedEvent
        {
            AgentId = command.AgentId.Trim(),
            TombstoneStateVersion = NextCommittedVersion(),
        });
    }

    [EventHandler]
    public async Task HandleExecutionUpdateAsync(AgentRegistryExecutionUpdateCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            Logger.LogWarning("Cannot update execution state with empty agent id");
            return;
        }

        if (State.Entries.All(x => !string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal)))
        {
            Logger.LogWarning("Cannot update execution state for missing agent registry entry: {AgentId}", command.AgentId);
            return;
        }

        await PersistDomainEventAsync(new AgentRegistryExecutionUpdatedEvent
        {
            AgentId = command.AgentId.Trim(),
            Status = command.Status?.Trim() ?? string.Empty,
            LastRunAt = command.LastRunAt,
            NextRunAt = command.NextRunAt,
            ErrorCount = command.ErrorCount,
            LastError = command.LastError?.Trim() ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleCompactTombstonesAsync(AgentRegistryCompactTombstonesCommand command)
    {
        if (command.SafeStateVersion <= 0)
            return;

        var agentIds = State.Entries
            .Where(static entry => entry.Tombstoned)
            .Where(entry => entry.TombstoneStateVersion > 0 && entry.TombstoneStateVersion <= command.SafeStateVersion)
            .Select(static entry => entry.AgentId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (agentIds.Length == 0)
            return;

        await PersistDomainEventAsync(new AgentRegistryTombstonesCompactedEvent
        {
            AgentIds = { agentIds },
            SafeStateVersion = command.SafeStateVersion,
        });
    }

    private static AgentRegistryState ApplyUpserted(AgentRegistryState current, AgentRegistryUpsertedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.Entry.AgentId, StringComparison.Ordinal));
        if (existing != null)
            next.Entries.Remove(existing);

        var entry = evt.Entry.Clone();
        entry.Tombstoned = false;
        entry.TombstoneStateVersion = 0;
        next.Entries.Add(entry);
        return next;
    }

    private static AgentRegistryState ApplyTombstoned(AgentRegistryState current, AgentRegistryTombstonedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing == null)
            return next;

        existing.Tombstoned = true;
        existing.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        existing.TombstoneStateVersion = evt.TombstoneStateVersion;
        return next;
    }

    private static AgentRegistryState ApplyExecutionUpdated(AgentRegistryState current, AgentRegistryExecutionUpdatedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.AgentId, StringComparison.Ordinal));
        if (existing == null)
            return next;

        existing.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        existing.Status = evt.Status ?? string.Empty;
        existing.LastRunAt = evt.LastRunAt;
        existing.NextRunAt = evt.NextRunAt;
        existing.ErrorCount = evt.ErrorCount;
        existing.LastError = evt.LastError ?? string.Empty;
        return next;
    }

    private static AgentRegistryState ApplyTombstonesCompacted(
        AgentRegistryState current,
        AgentRegistryTombstonesCompactedEvent evt)
    {
        if (evt.AgentIds.Count == 0)
            return current;

        var next = current.Clone();
        var compacted = evt.AgentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var removable = next.Entries
            .Where(entry => compacted.Contains(entry.AgentId))
            .ToArray();
        foreach (var entry in removable)
            next.Entries.Remove(entry);
        return next;
    }

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;

    private static string MergeNonEmpty(string? incoming, string? existing)
    {
        var normalizedIncoming = (incoming ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(normalizedIncoming)
            ? normalizedIncoming
            : (existing ?? string.Empty);
    }
}
