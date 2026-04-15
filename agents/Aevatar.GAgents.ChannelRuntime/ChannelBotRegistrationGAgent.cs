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
            .On<ChannelBotTokenUpdatedEvent>(ApplyTokenUpdated)
            .OrCurrent();

    // ─── Commands ───

    [EventHandler]
    public async Task HandleRegister(ChannelBotRegisterCommand cmd)
    {
        var entry = new ChannelBotRegistrationEntry
        {
            Id = !string.IsNullOrWhiteSpace(cmd.RequestedId) ? cmd.RequestedId : Guid.NewGuid().ToString("N"),
            Platform = cmd.Platform,
            NyxProviderSlug = cmd.NyxProviderSlug,
            NyxUserToken = cmd.NyxUserToken,
            VerificationToken = cmd.VerificationToken,
            ScopeId = cmd.ScopeId,
            WebhookUrl = cmd.WebhookUrl,
            EncryptKey = cmd.EncryptKey,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventAsync(new ChannelBotRegisteredEvent { Entry = entry });
        Logger.LogInformation("Registered channel bot: id={Id}, platform={Platform}, slug={Slug}",
            entry.Id, entry.Platform, entry.NyxProviderSlug);
    }

    [EventHandler]
    public async Task HandleUnregister(ChannelBotUnregisterCommand cmd)
    {
        var exists = State.Registrations.Any(r => r.Id == cmd.RegistrationId);
        if (!exists)
        {
            Logger.LogWarning("Cannot unregister: channel bot registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new ChannelBotUnregisteredEvent { RegistrationId = cmd.RegistrationId });
        Logger.LogInformation("Unregistered channel bot: id={Id}", cmd.RegistrationId);
    }

    [EventHandler]
    public async Task HandleUpdateToken(ChannelBotUpdateTokenCommand cmd)
    {
        var exists = State.Registrations.Any(r => r.Id == cmd.RegistrationId);
        if (!exists)
        {
            Logger.LogWarning("Cannot update token: channel bot registration not found: {Id}", cmd.RegistrationId);
            return;
        }

        await PersistDomainEventAsync(new ChannelBotTokenUpdatedEvent
        {
            RegistrationId = cmd.RegistrationId,
            NyxUserToken = cmd.NyxUserToken,
        });
        Logger.LogInformation("Updated token for channel bot: id={Id}", cmd.RegistrationId);
    }

    // ─── State transitions ───

    private static ChannelBotRegistrationStoreState ApplyRegistered(ChannelBotRegistrationStoreState current, ChannelBotRegisteredEvent evt)
    {
        var next = current.Clone();
        next.Registrations.Add(evt.Entry);
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyUnregistered(ChannelBotRegistrationStoreState current, ChannelBotUnregisteredEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is not null)
            next.Registrations.Remove(entry);
        return next;
    }

    private static ChannelBotRegistrationStoreState ApplyTokenUpdated(ChannelBotRegistrationStoreState current, ChannelBotTokenUpdatedEvent evt)
    {
        var next = current.Clone();
        var entry = next.Registrations.FirstOrDefault(r => r.Id == evt.RegistrationId);
        if (entry is not null)
            entry.NyxUserToken = evt.NyxUserToken;
        return next;
    }
}
