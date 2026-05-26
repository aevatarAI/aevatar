using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-user-per-bot-instance actor keyed by <c>{bot_instance_id}:{channel}:{sender_canonical_id}</c>.
/// Holds user-scoped credential binding and preferences so <see cref="ConversationGAgent"/> state
/// stays conversation-scoped. Split out from the legacy <c>ChannelUserGAgent</c> per RFC §5.2b.
/// </summary>
public sealed class ChannelUserBindingGAgent : GAgentBase<ChannelUserBindingState>
{
    /// <inheritdoc />
    protected override ChannelUserBindingState TransitionState(ChannelUserBindingState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserCredentialBoundEvent>(ApplyBound)
            .On<UserCredentialUnboundEvent>(ApplyUnbound)
            .On<UserPreferencesUpdatedEvent>(ApplyPreferencesUpdated)
            .OrCurrent();

    /// <summary>
    /// Binds one user credential reference.
    /// </summary>
    [EventHandler]
    public async Task HandleBindCredentialAsync(BindUserCredentialCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrWhiteSpace(cmd.CredentialRef))
        {
            Logger.LogWarning("Bind rejected: empty credential_ref for sender={Sender}", cmd.SenderCanonicalId);
            return;
        }

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(new UserCredentialBoundEvent
        {
            Bot = cmd.Bot?.Clone() ?? new BotInstanceId(),
            Channel = cmd.Channel?.Clone() ?? new ChannelId(),
            SenderCanonicalId = cmd.SenderCanonicalId,
            CredentialRef = cmd.CredentialRef,
            BoundAt = now,
        });
    }

    /// <summary>
    /// Unbinds the stored user credential reference.
    /// </summary>
    [EventHandler]
    public async Task HandleUnbindCredentialAsync(UnbindUserCredentialCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrWhiteSpace(State.CredentialRef))
        {
            Logger.LogInformation("Unbind skipped: no credential bound for sender={Sender}", cmd.SenderCanonicalId);
            return;
        }

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(new UserCredentialUnboundEvent
        {
            Bot = cmd.Bot?.Clone() ?? new BotInstanceId(),
            Channel = cmd.Channel?.Clone() ?? new ChannelId(),
            SenderCanonicalId = cmd.SenderCanonicalId,
            UnboundAt = now,
        });
    }

    /// <summary>
    /// Persists a typed preferences snapshot. Callers supply a full <see cref="ChannelUserPreferences"/>
    /// message; the grain replaces the stored snapshot atomically.
    /// </summary>
    [EventHandler]
    public async Task HandleUpdatePreferencesAsync(UpdateUserPreferencesCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(new UserPreferencesUpdatedEvent
        {
            Bot = cmd.Bot?.Clone() ?? new BotInstanceId(),
            Channel = cmd.Channel?.Clone() ?? new ChannelId(),
            SenderCanonicalId = cmd.SenderCanonicalId,
            Preferences = cmd.Preferences?.Clone() ?? new ChannelUserPreferences(),
            UpdatedAt = now,
        });
    }

    // ─── State transitions ───

    private static ChannelUserBindingState ApplyBound(ChannelUserBindingState current, UserCredentialBoundEvent evt)
    {
        var next = current.Clone();
        next.Bot = evt.Bot?.Clone();
        next.Channel = evt.Channel?.Clone();
        next.SenderCanonicalId = evt.SenderCanonicalId;
        next.CredentialRef = evt.CredentialRef;
        next.CreatedAt ??= evt.BoundAt;
        next.UpdatedAt = evt.BoundAt;
        return next;
    }

    private static ChannelUserBindingState ApplyUnbound(ChannelUserBindingState current, UserCredentialUnboundEvent evt)
    {
        var next = current.Clone();
        next.CredentialRef = string.Empty;
        next.UpdatedAt = evt.UnboundAt;
        return next;
    }

    private static ChannelUserBindingState ApplyPreferencesUpdated(ChannelUserBindingState current, UserPreferencesUpdatedEvent evt)
    {
        var next = current.Clone();
        next.Bot = evt.Bot?.Clone();
        next.Channel = evt.Channel?.Clone();
        next.SenderCanonicalId = evt.SenderCanonicalId;
        next.Preferences = evt.Preferences?.Clone() ?? new ChannelUserPreferences();
        next.CreatedAt ??= evt.UpdatedAt;
        next.UpdatedAt = evt.UpdatedAt;
        return next;
    }
}
