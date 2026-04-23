using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

public sealed class TelegramChannelAdapterConformanceTests
    : ChannelAdapterConformanceTests<TelegramChannelAdapter>
{
    private readonly TelegramAdapterHarness _harness = new();

    protected override TelegramChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;

    protected override ChannelTransportBinding? CreateSecondaryBinding() => _harness.SecondaryBinding;

    protected override ConversationReference BuildDirectMessageReference(TelegramChannelAdapter adapter) =>
        ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42");
}
