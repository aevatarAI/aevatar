using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelPlatformBehaviorConformanceTests
{
    [Fact]
    public async Task MatrixWithoutAdapters_ParsesPrivateTextAndRepliesWithoutSlugOrSender()
    {
        var parsed = new NyxIdRelayTransport().Parse(Encoding.UTF8.GetBytes("""
            {"message_id":"msg-matrix","platform":" MATRIX ","agent":{"api_key_id":"key-matrix"},
             "conversation":{"type":"private"},"content":{"type":"text","text":"hello matrix"}}
            """));
        parsed.Success.Should().BeTrue();
        parsed.Activity!.ChannelId.Value.Should().Be("matrix");
        parsed.Activity.From.CanonicalId.Should().BeEmpty();
        parsed.Activity.Conversation.Partition.Should().Be("matrix-conversation");
        parsed.Activity.TransportExtras.NyxProviderSlug.Should().BeEmpty();
        var handler = new ReplyHandler();
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));
        var outbound = new NyxIdRelayOutboundPort(client, NullLogger<NyxIdRelayOutboundPort>.Instance, []);
        var sent = await outbound.SendAsync("matrix", parsed.Activity.Conversation,
            new MessageContent { Text = "plain reply" }, parsed.Activity.OutboundDelivery, "reply-authority", CancellationToken.None);
        sent.Success.Should().BeTrue();
        handler.Body.Should().Contain("plain reply").And.Contain("msg-matrix");
        handler.Path.Should().Be("/api/v1/channel-relay/reply");
        var missing = await outbound.SendAsync("matrix", parsed.Activity.Conversation,
            new MessageContent { Text = "plain reply" }, parsed.Activity.OutboundDelivery, string.Empty, CancellationToken.None);
        missing.ErrorCode.Should().Be("reply_token_missing_or_expired");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void DuplicateOptionalBehavior_IsRejectedRatherThanSelectedArbitrarily()
    {
        var construct = () => new NyxIdRelayTransport([new LarkRelayMessageAdapter(), new LarkRelayMessageAdapter()]);
        construct.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*ContentAdapter*lark*");
        var groups = () => ChannelPlatformBehavior.Validate<IChannelGroupAdmissionPolicy>(
            [new TelegramGroupAdmissionPolicy(), new TelegramGroupAdmissionPolicy()]);
        groups.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PlatformRegistration_IsIdempotentAndExposesExpectedSurfacesAndDescriptors()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddLarkPlatform().AddLarkPlatform().AddTelegramPlatform().AddTelegramPlatform();
        using var provider = services.BuildServiceProvider();
        ChannelPlatformBehavior.Validate(provider.GetServices<IChannelGroupAdmissionPolicy>()).Should().HaveCount(3);
        ChannelPlatformBehavior.Validate(provider.GetServices<IChannelTypingIndicator>()).Should().HaveCount(2);
        ChannelPlatformBehavior.Validate(provider.GetServices<INyxIdRelayContentAdapter>()).Should().HaveCount(2);
        provider.GetServices<INyxIdRelayInteractionAdapter>().Select(x => x.Platform).Should().BeEquivalentTo("lark", "feishu", "telegram");
        provider.GetServices<IMessageComposer>().Select(x => x.Channel.Value).Should().BeEquivalentTo("lark", "feishu", "telegram");
        provider.GetServices<IChannelNativeMessageSender>().Should().HaveCount(2);
        TelegramMessageComposer.DefaultCapabilities.SupportsDelete.Should().BeFalse();
    }

    [Fact]
    public void ContentAdapterCannotRewriteAuthenticatedIdentity()
    {
        var transport = new NyxIdRelayTransport([new MutatingContentAdapter()]);
        var result = transport.Parse(Encoding.UTF8.GetBytes("""
            {"message_id":"original-message","platform":"matrix","agent":{"api_key_id":"original-key"},
             "conversation":{"type":"private"},"content":{"type":"text","text":"hello"}}
            """));
        result.Success.Should().BeTrue();
        result.Activity!.Id.Should().Be("original-message");
        result.Activity.ChannelId.Value.Should().Be("matrix");
        result.Activity.TransportExtras.NyxAgentApiKeyId.Should().Be("original-key");
    }

    private sealed class MutatingContentAdapter : INyxIdRelayContentAdapter
    {
        public string Platform => "matrix";
        public bool SupportsContent(NyxIdRelayCallbackPayload payload) => true;

        public MessageContent NormalizeContent(NyxIdRelayCallbackPayload payload)
        {
            payload.Platform = "attacker";
            payload.MessageId = "attacker";
            payload.Agent!.ApiKeyId = "attacker";
            return new MessageContent { Text = "normalized" };
        }
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string Path { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"message_id\":\"reply-matrix\"}") };
        }
    }
}
