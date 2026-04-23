using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Aevatar.GAgents.Channel.Testing;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkChannelAdapterConformanceTests : ChannelAdapterConformanceTests<LarkChannelAdapter>
{
    private readonly LarkAdapterHarness _harness = new();

    protected override LarkChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;

    protected override InboundActivitySeed? BuildBotMentionSeed(ChannelTransportBinding binding) => new(
        ActivityType.Message,
        ConversationScope.DirectMessage,
        ConversationKey: "dm-mention",
        SenderCanonicalId: "user-1",
        SenderDisplayName: "User One",
        Text: $"hi <at user_id=\"{binding.Bot.Bot.Value}\">Bot</at> please help");

    protected override InboundActivitySeed? BuildParticipantMentionSeed() => new(
        ActivityType.Message,
        ConversationScope.DirectMessage,
        ConversationKey: "dm-other",
        SenderCanonicalId: "user-1",
        SenderDisplayName: "User One",
        Text: "ping <at user_id=\"other-user\">Other User</at> please review");
}
