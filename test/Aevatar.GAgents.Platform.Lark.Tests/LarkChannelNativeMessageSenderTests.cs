using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Shouldly;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkChannelNativeMessageSenderTests
{
    [Fact]
    public async Task SendAsync_ShouldUseLarkOwnedTypedRoute_WhenTargetExposesRoute()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new TestLarkDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "legacy-conversation",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1",
            LarkReceiveId: "oc_dm_chat_1",
            LarkReceiveIdType: "chat_id",
            LarkReceiveIdFallback: "on_user_1",
            LarkReceiveIdTypeFallback: "union_id");

        await sender.SendAsync(
            target,
            new ChannelNativeMessage("hello", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            CancellationToken.None);

        dispatcher.LastRequest.ShouldNotBeNull();
        dispatcher.LastRequest!.PrimaryTarget.ShouldBe(new LarkReceiveTarget(
            "oc_dm_chat_1",
            "chat_id",
            FellBackToPrefixInference: false));
        dispatcher.LastRequest.FallbackTarget.ShouldBe(new LarkReceiveTarget(
            "on_user_1",
            "union_id",
            FellBackToPrefixInference: false));
    }

    [Fact]
    public async Task SendAsync_ShouldInferFromNeutralConversationId_WhenTargetHasNoLarkRoute()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new ChannelNativeDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "on_user_1",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1");

        await sender.SendAsync(
            target,
            new ChannelNativeMessage("hello", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            CancellationToken.None);

        dispatcher.LastRequest.ShouldNotBeNull();
        dispatcher.LastRequest!.PrimaryTarget.ShouldBe(new LarkReceiveTarget(
            "on_user_1",
            "union_id",
            FellBackToPrefixInference: false));
        dispatcher.LastRequest.FallbackTarget.ShouldBeNull();
    }

    private sealed class RecordingLarkOutboundDispatcher : ILarkOutboundDispatcher
    {
        public LarkSendNewMessageRequest? LastRequest { get; private set; }

        public Task<LarkSendNewMessageResult> SendNewMessageAsync(
            LarkSendNewMessageRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(LarkSendNewMessageResult.Sent(
                "om_1",
                request.PrimaryTarget,
                usedFallback: false));
        }
    }

    private sealed record TestLarkDeliveryTarget(
        string AgentId,
        string Platform,
        string ConversationId,
        string NyxProviderSlug,
        string NyxApiKey,
        string LarkReceiveId,
        string LarkReceiveIdType,
        string LarkReceiveIdFallback,
        string LarkReceiveIdTypeFallback)
        : ChannelNativeDeliveryTarget(
            AgentId,
            Platform,
            ConversationId,
            NyxProviderSlug,
            NyxApiKey),
            ILarkChannelNativeDeliveryRoute;
}
