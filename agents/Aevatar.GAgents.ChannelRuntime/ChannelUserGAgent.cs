using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Per-sender coordinator actor. Owns identity state and orchestrates the
/// full inbound message flow: track identity → resolve token → dispatch to
/// chat actor → collect response → send reply via platform adapter.
///
/// Actor ID convention: channel-user-{platform}-{registrationId}-{senderId}
///
/// Token is stored in actor state (not projected to any readmodel) to keep
/// credentials internal to the actor boundary.
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
        // 1. Track sender identity
        await PersistDomainEventAsync(new ChannelUserTrackedEvent
        {
            Platform = evt.Platform,
            PlatformUserId = evt.SenderId,
            DisplayName = evt.SenderName,
        });

        // 2. Resolve effective token: bound token > registration fallback
        var effectiveToken = !string.IsNullOrEmpty(State.NyxidAccessToken)
            ? State.NyxidAccessToken
            : evt.RegistrationToken;

        // 3. Get or create chat actor (per sender)
        var chatActorId = $"channel-{evt.Platform}-{evt.RegistrationId}-{evt.SenderId}";
        var actorRuntime = Services.GetRequiredService<IActorRuntime>();
        var chatActor = await actorRuntime.GetAsync(chatActorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(chatActorId);

        // 4. Build and dispatch ChatRequestEvent
        var sessionId = Guid.NewGuid().ToString("N");
        var chatRequest = new ChatRequestEvent
        {
            Prompt = evt.Text,
            SessionId = sessionId,
            ScopeId = evt.RegistrationScopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken;
        chatRequest.Metadata["scope_id"] = evt.RegistrationScopeId;
        chatRequest.Metadata["channel.platform"] = evt.Platform;
        chatRequest.Metadata["channel.sender_id"] = evt.SenderId;
        chatRequest.Metadata["channel.sender_name"] = evt.SenderName;
        chatRequest.Metadata["channel.message_id"] = evt.MessageId;
        chatRequest.Metadata["channel.chat_type"] = evt.ChatType;
        chatRequest.Metadata["channel.user_actor_id"] = Id;

        // 5. Subscribe to response stream
        var subscriptionProvider = Services.GetRequiredService<IActorEventSubscriptionProvider>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseBuilder = new StringBuilder();
        using var ctr = cts.Token.Register(() => responseTcs.TrySetCanceled());

        await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
            chatActor.Id,
            envelope =>
            {
                var payload = envelope.Payload;
                if (payload is null) return Task.CompletedTask;

                if (payload.Is(TextMessageContentEvent.Descriptor))
                {
                    var contentEvt = payload.Unpack<TextMessageContentEvent>();
                    if (contentEvt.SessionId == sessionId && !string.IsNullOrEmpty(contentEvt.Delta))
                        responseBuilder.Append(contentEvt.Delta);
                }
                else if (payload.Is(TextMessageEndEvent.Descriptor))
                {
                    var endEvt = payload.Unpack<TextMessageEndEvent>();
                    if (endEvt.SessionId == sessionId)
                        responseTcs.TrySetResult(responseBuilder.ToString());
                }

                return Task.CompletedTask;
            },
            cts.Token);

        var chatEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = chatActor.Id },
            },
        };

        await chatActor.HandleEventAsync(chatEnvelope, cts.Token);

        // 6. Wait for response
        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(120_000, cts.Token));

        string replyText;
        if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
        {
            replyText = responseTcs.Task.Result;
            Logger.LogInformation(
                "Channel response ready: platform={Platform}, sender={SenderId}, length={Length}",
                evt.Platform, evt.SenderId, replyText.Length);
        }
        else
        {
            var partial = responseBuilder.ToString();
            replyText = partial.Length > 0
                ? partial
                : "Sorry, it's taking too long to respond. Please try again.";
            Logger.LogWarning(
                "Channel response timed out: platform={Platform}, sender={SenderId}",
                evt.Platform, evt.SenderId);
        }

        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";

        // 7. Send reply via platform adapter
        var adapters = Services.GetRequiredService<IEnumerable<IPlatformAdapter>>();
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, evt.Platform, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            Logger.LogWarning("No adapter for platform: {Platform}", evt.Platform);
            return;
        }

        var nyxClient = Services.GetRequiredService<NyxIdApiClient>();
        var inbound = new InboundMessage
        {
            Platform = evt.Platform,
            ConversationId = evt.ConversationId,
            SenderId = evt.SenderId,
            SenderName = evt.SenderName,
            Text = evt.Text,
            MessageId = evt.MessageId,
            ChatType = evt.ChatType,
        };

        // Reconstruct a minimal registration entry for the adapter's SendReply
        var registration = new ChannelBotRegistrationEntry
        {
            Id = evt.RegistrationId,
            Platform = evt.Platform,
            NyxProviderSlug = evt.NyxProviderSlug,
            NyxUserToken = effectiveToken,
            ScopeId = evt.RegistrationScopeId,
        };

        await adapter.SendReplyAsync(replyText, inbound, registration, nyxClient, cts.Token);
    }

    // ─── State transitions ───

    private static ChannelUserState ApplyTracked(ChannelUserState current, ChannelUserTrackedEvent evt)
    {
        var next = current.Clone();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(next.Platform))
        {
            // First time — set immutable identity fields
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
