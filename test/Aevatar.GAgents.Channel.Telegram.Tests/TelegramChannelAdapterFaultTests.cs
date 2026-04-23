using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

public sealed class TelegramChannelAdapterFaultTests : ChannelAdapterFaultTests<TelegramChannelAdapter>
{
    private readonly TelegramAdapterHarness _harness = new();

    protected override TelegramChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;

    protected override IPayloadRedactor? Redactor => _harness.Redactor;

    protected override StreamingFaultProbe? StreamingProbe => _harness.StreamingProbe;
}
