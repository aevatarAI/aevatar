using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Per-sender identity actor. Owns sender identity state (platform userId,
/// display name, NyxID binding). Stateful — persists domain events.
///
/// Chat dispatch and reply coordination is handled outside the grain
/// (in the HTTP endpoint layer) to avoid Orleans grain scheduler deadlock
/// when subscribing to another actor's stream within a grain turn.
///
/// Actor ID convention: channel-user-{platform}-{registrationId}-{senderId}
/// </summary>
public sealed class ChannelUserGAgent : GAgentBase<ChannelUserState>
{
    protected override ChannelUserState TransitionState(ChannelUserState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelUserTrackedEvent>(ApplyTracked)
            .On<ChannelUserBoundEvent>(ApplyBound)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInbound(ChannelInboundEvent evt)
    {
        // Track sender identity — the only stateful responsibility of this actor.
        await PersistDomainEventAsync(new ChannelUserTrackedEvent
        {
            Platform = evt.Platform,
            PlatformUserId = evt.SenderId,
            DisplayName = evt.SenderName,
        });
    }

    /// <summary>
    /// Returns the effective NyxID token for this user.
    /// If the user has bound their own NyxID account, returns that token;
    /// otherwise returns null (caller should fall back to org token).
    /// </summary>
    public string? GetBoundUserToken() =>
        !string.IsNullOrEmpty(State.NyxidAccessToken) ? State.NyxidAccessToken : null;

    // ─── State transitions ───

    private static ChannelUserState ApplyTracked(ChannelUserState current, ChannelUserTrackedEvent evt)
    {
        var next = current.Clone();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(next.Platform))
        {
            next.Platform = evt.Platform;
            next.PlatformUserId = evt.PlatformUserId;
            next.FirstSeen = now;
        }

        next.DisplayName = evt.DisplayName;
        next.LastSeen = now;
        return next;
    }

    private static ChannelUserState ApplyBound(ChannelUserState current, ChannelUserBoundEvent evt)
    {
        var next = current.Clone();
        next.NyxidUserId = evt.NyxidUserId;
        next.NyxidAccessToken = evt.NyxidAccessToken;
        return next;
    }
}
