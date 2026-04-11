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

        // 2. Resolve tokens: user token (if bound) + org token (always from registration)
        var userToken = !string.IsNullOrEmpty(State.NyxidAccessToken)
            ? State.NyxidAccessToken
            : null;
        var orgToken = evt.RegistrationToken;
        var effectiveToken = userToken ?? orgToken;

        // 3. Dispatch to chat actor and send reply
        try
        {
            await DispatchChatAndReplyAsync(evt, effectiveToken, orgToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleInbound failed: platform={Platform}, sender={SenderId}", evt.Platform, evt.SenderId);
            // Send error back to user via bot so we can see what broke
            try
            {
                await SendReplyAsync(evt, $"[Error] {ex.GetType().Name}: {ex.Message}", orgToken);
            }
            catch (Exception replyEx)
            {
                // If sending the error reply also fails, log both errors
                // so the failure is never completely silent.
                Logger.LogError(replyEx,
                    "SendReplyAsync also failed while reporting error: platform={Platform}, sender={SenderId}, originalError={OriginalError}",
                    evt.Platform, evt.SenderId, ex.Message);
            }
        }
    }

    // ─── Chat dispatch + reply ───

    private async Task DispatchChatAndReplyAsync(
        ChannelInboundEvent evt, string effectiveToken, string orgToken)
    {
        // Get or create chat actor (per sender)
        var chatActorId = $"channel-{evt.Platform}-{evt.RegistrationId}-{evt.SenderId}";
        var actorRuntime = Services.GetRequiredService<IActorRuntime>();
        var chatActor = await actorRuntime.GetAsync(chatActorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(chatActorId);

        // Build and dispatch ChatRequestEvent
        var sessionId = Guid.NewGuid().ToString("N");
        var chatRequest = new ChatRequestEvent
        {
            Prompt = evt.Text,
            SessionId = sessionId,
            ScopeId = evt.RegistrationScopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken;
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = orgToken;
        chatRequest.Metadata["scope_id"] = evt.RegistrationScopeId;
        chatRequest.Metadata[ChannelMetadataKeys.Platform] = evt.Platform;
        chatRequest.Metadata[ChannelMetadataKeys.SenderId] = evt.SenderId;
        chatRequest.Metadata[ChannelMetadataKeys.SenderName] = evt.SenderName;
        chatRequest.Metadata[ChannelMetadataKeys.MessageId] = evt.MessageId;
        chatRequest.Metadata[ChannelMetadataKeys.ChatType] = evt.ChatType;
        chatRequest.Metadata[ChannelMetadataKeys.UserActorId] = Id;

        // Subscribe to response stream and wait
        var replyText = await CollectChatResponseAsync(chatActor, chatRequest, sessionId);

        // Send reply via platform adapter — always uses org token for bot API
        await SendReplyAsync(evt, replyText, orgToken);
    }

    private async Task<string> CollectChatResponseAsync(
        IActor chatActor, ChatRequestEvent chatRequest, string sessionId)
    {
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

        // Wait for response
        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(120_000, cts.Token));

        if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
        {
            var text = responseTcs.Task.Result;
            Logger.LogInformation(
                "Channel response ready: platform={Platform}, length={Length}",
                chatRequest.Metadata["channel.platform"], text.Length);
            return text;
        }

        var partial = responseBuilder.ToString();
        Logger.LogWarning(
            "Channel response timed out: platform={Platform}",
            chatRequest.Metadata["channel.platform"]);
        return partial.Length > 0
            ? partial
            : "Sorry, it's taking too long to respond. Please try again.";
    }

    private async Task SendReplyAsync(
        ChannelInboundEvent evt, string replyText, string orgToken)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";

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

        // Reply via bot API — always uses org token, not user's personal token.
        // The bot credentials (api-telegram-bot, api-lark-bot) belong to the org.
        var registration = new ChannelBotRegistrationEntry
        {
            Id = evt.RegistrationId,
            Platform = evt.Platform,
            NyxProviderSlug = evt.NyxProviderSlug,
            NyxUserToken = orgToken,
            ScopeId = evt.RegistrationScopeId,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
