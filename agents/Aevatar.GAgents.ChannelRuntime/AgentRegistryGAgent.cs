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
            .On<AgentRegistryTombstonedEvent>(ApplyTombstoned)
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
            Platform = command.Platform?.Trim() ?? string.Empty,
            ConversationId = command.ConversationId?.Trim() ?? string.Empty,
            NyxProviderSlug = command.NyxProviderSlug?.Trim() ?? string.Empty,
            NyxApiKey = command.NyxApiKey?.Trim() ?? string.Empty,
            OwnerNyxUserId = command.OwnerNyxUserId?.Trim() ?? string.Empty,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Tombstoned = false,
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
        });
    }

    private static AgentRegistryState ApplyUpserted(AgentRegistryState current, AgentRegistryUpsertedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Entries.FirstOrDefault(x => string.Equals(x.AgentId, evt.Entry.AgentId, StringComparison.Ordinal));
        if (existing != null)
            next.Entries.Remove(existing);

        next.Entries.Add(evt.Entry);
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
        return next;
    }
}
