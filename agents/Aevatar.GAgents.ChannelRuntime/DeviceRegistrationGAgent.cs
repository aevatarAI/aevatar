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
        var exists = State.Registrations.Any(r => r.Id == cmd.RegistrationId);
        if (!exists)
        {
            Logger.LogWarning("Cannot unregister: device registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new DeviceUnregisteredEvent { RegistrationId = cmd.RegistrationId });
        Logger.LogInformation("Unregistered device: id={Id}", cmd.RegistrationId);
    }

    // ─── State transitions ───

    private static DeviceRegistrationState ApplyRegistered(DeviceRegistrationState current, DeviceRegisteredEvent evt)
    {
        var next = current.Clone();
        next.Registrations.Add(evt.Entry);
        return next;
    }

    private static DeviceRegistrationState ApplyUnregistered(DeviceRegistrationState current, DeviceUnregisteredEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is not null)
            next.Registrations.Remove(entry);
        return next;
    }
}
