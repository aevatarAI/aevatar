using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Shouldly;
using ChannelAddressModel = Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddress;
using ChannelAddressEndpointModel = Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddressEndpoint;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkChannelNativeMessageSenderTests
{
    [Fact]
    public async Task SendAsync_ShouldUseLarkOwnedTypedRoute_WhenTargetExposesRoute()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new TestLarkRouteDeliveryTarget(
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

    [Fact]
    public async Task SendAsync_ShouldUseLarkAdapterRoute_WhenTargetCarriesUserAgentDeliveryTarget()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var adapter = new LarkChannelNativeDeliveryTargetAdapter();
        var target = adapter.Adapt(new UserAgentDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "legacy-conversation",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1",
            ChannelAddress: new ChannelAddressModel(
                "lark",
                "api-lark-bot",
                "legacy-conversation",
                new ChannelAddressEndpointModel("oc_dm_chat_1", "chat_id"),
                new ChannelAddressEndpointModel("on_user_1", "union_id")),
            OutputFormat: ScheduledAgentOutputFormat.Auto,
            TemplateName: string.Empty,
            AgentType: string.Empty));

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
    public async Task SendAsync_ShouldUseGenericChannelAddress_WhenTargetCarriesUserAgentDeliveryTarget()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new UserAgentDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "legacy-conversation",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1",
            ChannelAddress: new ChannelAddressModel(
                "lark",
                "api-lark-bot",
                "legacy-conversation",
                new ChannelAddressEndpointModel("oc_dm_chat_1", "chat_id"),
                new ChannelAddressEndpointModel("on_user_1", "union_id")),
            OutputFormat: ScheduledAgentOutputFormat.Auto,
            TemplateName: string.Empty,
            AgentType: string.Empty);

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
    public async Task UpdateAsync_ShouldDelegateTextEditToLarkDispatcher()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher();
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new ChannelNativeDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "oc_chat_1",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1");

        var result = await sender.UpdateAsync(
            target,
            "om_stream",
            new ChannelNativeMessage("hello updated", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            isFinal: false,
            cancellationToken: CancellationToken.None);

        result.SentActivityId.ShouldBe("om_stream");
        result.PlatformMessageId.ShouldBe("om_stream");
        dispatcher.LastUpdateRequest.ShouldNotBeNull();
        dispatcher.LastUpdateRequest!.MessageId.ShouldBe("om_stream");
        dispatcher.LastUpdateRequest.MessageType.ShouldBe("text");
        dispatcher.LastUpdateRequest.ContentJson.ShouldContain("hello updated");
    }

    [Fact]
    public async Task UpdateAsync_WhenEditCapReachedMidStream_ReturnsSealedFailure()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher
        {
            NextUpdateResult = LarkUpdateMessageResult.Failed(
                230072,
                "The message has reached the number of times it can be edited."),
        };
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new ChannelNativeDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "oc_chat_1",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1");

        var result = await sender.UpdateAsync(
            target,
            "om_stream",
            new ChannelNativeMessage("hello updated", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            isFinal: false,
            cancellationToken: CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("message_update_sealed");
        result.RawErrorCode.ShouldBe(230072);
        dispatcher.UpdateRequests.Count.ShouldBe(1);
        dispatcher.SendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FinalEdit_EditCapReached_PostsCompleteTextAsFreshMessage_DoesNotThrow()
    {
        var dispatcher = new RecordingLarkOutboundDispatcher
        {
            NextUpdateResult = LarkUpdateMessageResult.Failed(
                230072,
                "The message has reached the number of times it can be edited."),
            NextSendMessageId = "om_final",
        };
        var sender = new LarkChannelNativeMessageSender(dispatcher);
        var target = new ChannelNativeDeliveryTarget(
            AgentId: "agent-1",
            Platform: "lark",
            ConversationId: "oc_chat_1",
            NyxProviderSlug: "api-lark-bot",
            NyxApiKey: "nyx-api-key-1");

        var result = await sender.UpdateAsync(
            target,
            "om_stream",
            new ChannelNativeMessage("complete final text", CardPayload: null, MessageType: "text", ComposeCapability.Exact),
            isFinal: true,
            cancellationToken: CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.SentActivityId.ShouldBe("om_final");
        result.PlatformMessageId.ShouldBe("om_final");
        dispatcher.UpdateRequests.Count.ShouldBe(1);
        dispatcher.SendRequests.Count.ShouldBe(1);
        dispatcher.SendRequests[0].ContentJson.ShouldContain("complete final text");
    }

    private sealed class RecordingLarkOutboundDispatcher : ILarkOutboundDispatcher
    {
        public LarkSendNewMessageRequest? LastRequest { get; private set; }
        public LarkUpdateMessageRequest? LastUpdateRequest { get; private set; }
        public List<LarkSendNewMessageRequest> SendRequests { get; } = [];
        public List<LarkUpdateMessageRequest> UpdateRequests { get; } = [];
        public LarkUpdateMessageResult? NextUpdateResult { get; set; }
        public string NextSendMessageId { get; set; } = "om_1";

        public Task<LarkSendNewMessageResult> SendNewMessageAsync(
            LarkSendNewMessageRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
            SendRequests.Add(request);
            return Task.FromResult(LarkSendNewMessageResult.Sent(
                NextSendMessageId,
                request.PrimaryTarget,
                usedFallback: false));
        }

        public Task<LarkUpdateMessageResult> UpdateMessageAsync(
            LarkUpdateMessageRequest request,
            CancellationToken ct)
        {
            LastUpdateRequest = request;
            UpdateRequests.Add(request);
            return Task.FromResult(NextUpdateResult ?? LarkUpdateMessageResult.Updated(request.MessageId));
        }
    }

    private sealed record TestLarkRouteDeliveryTarget(
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
