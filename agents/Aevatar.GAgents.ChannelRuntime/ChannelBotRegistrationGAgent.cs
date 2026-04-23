using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Actor-backed channel bot registration store.
/// State is event-sourced and persisted in the cluster event store — no local
/// filesystem dependency. Suitable for cloud deployment.
///
/// Actor ID convention: a single well-known instance "channel-bot-registration-store".
/// CLAUDE.md: "long-lived actor for fact owners: definition/catalog/manager/index"
/// </summary>
public sealed class ChannelBotRegistrationGAgent : GAgentBase<ChannelBotRegistrationStoreState>
{
    public const string WellKnownId = "channel-bot-registration-store";

    protected override ChannelBotRegistrationStoreState TransitionState(ChannelBotRegistrationStoreState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelBotRegisteredEvent>(ApplyRegistered)
            .On<ChannelBotUnregisteredEvent>(ApplyUnregistered)
            .On<ChannelBotTombstonesCompactedEvent>(ApplyTombstonesCompacted)
            .OrCurrent();

    // ─── Commands ───

    [EventHandler]
    public async Task HandleRegister(ChannelBotRegisterCommand cmd)
    {
        if (!string.Equals(cmd.Platform, "lark", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(
                "Ignoring retired direct-callback registration request: platform={Platform}, requestedId={RequestedId}",
                cmd.Platform,
                cmd.RequestedId);
            return;
        }

        var entry = new ChannelBotRegistrationEntry
        {
            Id = !string.IsNullOrWhiteSpace(cmd.RequestedId) ? cmd.RequestedId : Guid.NewGuid().ToString("N"),
            Platform = cmd.Platform,
            NyxProviderSlug = cmd.NyxProviderSlug,
            ScopeId = cmd.ScopeId,
            WebhookUrl = cmd.WebhookUrl,
            NyxChannelBotId = cmd.NyxChannelBotId ?? string.Empty,
            NyxAgentApiKeyId = cmd.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationRouteId = cmd.NyxConversationRouteId ?? string.Empty,
            NyxAgentApiKeyHash = cmd.NyxAgentApiKeyHash ?? string.Empty,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventAsync(new ChannelBotRegisteredEvent { Entry = entry });
        Logger.LogInformation("Registered channel bot: id={Id}, platform={Platform}, slug={Slug}",
            entry.Id, entry.Platform, entry.NyxProviderSlug);
    }

    [EventHandler]
    public async Task HandleUnregister(ChannelBotUnregisterCommand cmd)
    {
        var entry = State.Registrations.FirstOrDefault(r => r.Id == cmd.RegistrationId);
        if (entry is null || entry.Tombstoned)
        {
            Logger.LogWarning("Cannot unregister: channel bot registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new ChannelBotUnregisteredEvent
        {
            RegistrationId = cmd.RegistrationId,
            TombstoneStateVersion = NextCommittedVersion(),
        });
        Logger.LogInformation("Unregistered channel bot: id={Id}", cmd.RegistrationId);
    }

    [EventHandler]
    public async Task HandleCompactTombstones(ChannelBotCompactTombstonesCommand cmd)
    {
        if (cmd.SafeStateVersion <= 0)
            return;

        var registrationIds = State.Registrations
            .Where(static entry => entry.Tombstoned)
            .Where(entry => entry.TombstoneStateVersion > 0 && entry.TombstoneStateVersion <= cmd.SafeStateVersion)
            .Select(static entry => entry.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (registrationIds.Length == 0)
            return;

        await PersistDomainEventAsync(new ChannelBotTombstonesCompactedEvent
        {
            RegistrationIds = { registrationIds },
            SafeStateVersion = cmd.SafeStateVersion,
        });
    }

    // ─── State transitions ───

    private static ChannelBotRegistrationStoreState ApplyRegistered(ChannelBotRegistrationStoreState current, ChannelBotRegisteredEvent evt)
    {
        var next = current.Clone();
        var existing = next.Registrations.FirstOrDefault(r => r.Id == evt.Entry.Id);
        if (existing is not null)
            next.Registrations.Remove(existing);
        var entry = evt.Entry.Clone();
        entry.Tombstoned = false;
        entry.TombstoneStateVersion = 0;
        next.Registrations.Add(entry);
        return next;
    }

    // Soft-delete to retain the entry until the durable projector watermark
    // has advanced past this state version (Channel RFC §7.1.1).
    private static ChannelBotRegistrationStoreState ApplyUnregistered(ChannelBotRegistrationStoreState current, ChannelBotUnregisteredEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is not null)
        {
            entry.Tombstoned = true;
            entry.TombstoneStateVersion = evt.TombstoneStateVersion;
        }
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyTombstonesCompacted(
        ChannelBotRegistrationStoreState current,
        ChannelBotTombstonesCompactedEvent evt)
    {
        if (evt.RegistrationIds.Count == 0)
            return current;

        var next = current.Clone();
        var compacted = evt.RegistrationIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var removable = next.Registrations
            .Where(entry => compacted.Contains(entry.Id))
            .ToArray();
        foreach (var entry in removable)
            next.Registrations.Remove(entry);
        return next;
    }

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;

}
