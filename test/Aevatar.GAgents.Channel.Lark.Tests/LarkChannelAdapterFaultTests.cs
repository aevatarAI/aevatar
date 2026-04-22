using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Aevatar.GAgents.Channel.Testing;

namespace Aevatar.GAgents.Channel.Lark.Tests;

public sealed class LarkChannelAdapterFaultTests : ChannelAdapterFaultTests<LarkChannelAdapter>
{
    private readonly LarkAdapterHarness _harness = new();

    protected override LarkChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;

    protected override IPayloadRedactor? Redactor => _harness.Redactor;

    protected override StreamingFaultProbe? StreamingProbe => _harness.StreamingProbe;
}
