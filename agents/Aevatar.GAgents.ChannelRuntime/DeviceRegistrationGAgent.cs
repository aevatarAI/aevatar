using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Actor-backed device registration store.
/// State is event-sourced and persisted in the cluster event store — no local
/// filesystem dependency. Suitable for cloud deployment.
///
/// Actor ID convention: a single well-known instance "device-registration-store".
/// CLAUDE.md: "长期 actor 限定事实拥有者：definition/catalog/manager/index"
/// </summary>
public sealed class DeviceRegistrationGAgent : GAgentBase<DeviceRegistrationState>
{
    public const string WellKnownId = "device-registration-store";

    protected override DeviceRegistrationState TransitionState(DeviceRegistrationState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<DeviceRegisteredEvent>(ApplyRegistered)
            .On<DeviceUnregisteredEvent>(ApplyUnregistered)
            .On<DeviceTombstonesCompactedEvent>(ApplyTombstonesCompacted)
            .OrCurrent();

    // ─── Commands ───

    [EventHandler]
    public async Task HandleRegister(DeviceRegisterCommand cmd)
    {
        var entry = new DeviceRegistrationEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ScopeId = cmd.ScopeId,
            HmacKey = cmd.HmacKey,
            NyxConversationId = cmd.NyxConversationId,
            Description = cmd.Description,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventAsync(new DeviceRegisteredEvent { Entry = entry });
        Logger.LogInformation("Registered device: id={Id}, scope={ScopeId}", entry.Id, entry.ScopeId);
    }

    [EventHandler]
    public async Task HandleUnregister(DeviceUnregisterCommand cmd)
    {
        var entry = State.Registrations.FirstOrDefault(r => r.Id == cmd.RegistrationId);
        if (entry is null || entry.Tombstoned)
        {
            Logger.LogWarning("Cannot unregister: device registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new DeviceUnregisteredEvent
        {
            RegistrationId = cmd.RegistrationId,
            TombstoneStateVersion = NextCommittedVersion(),
        });
        Logger.LogInformation("Unregistered device: id={Id}", cmd.RegistrationId);
    }

    [EventHandler]
    public async Task HandleCompactTombstones(DeviceCompactTombstonesCommand cmd)
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

        await PersistDomainEventAsync(new DeviceTombstonesCompactedEvent
        {
            RegistrationIds = { registrationIds },
            SafeStateVersion = cmd.SafeStateVersion,
        });
    }

    // ─── State transitions ───

    private static DeviceRegistrationState ApplyRegistered(DeviceRegistrationState current, DeviceRegisteredEvent evt)
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
    private static DeviceRegistrationState ApplyUnregistered(DeviceRegistrationState current, DeviceUnregisteredEvent evt)
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

    private static DeviceRegistrationState ApplyTombstonesCompacted(
        DeviceRegistrationState current,
        DeviceTombstonesCompactedEvent evt)
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
