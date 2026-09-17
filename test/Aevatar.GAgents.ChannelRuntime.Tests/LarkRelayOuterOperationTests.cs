using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkRelayOuterOperationTests
{
    [Theory]
    [InlineData("message_delete", "post", "private")]
    [InlineData("message_delete", "text", "private")]
    [InlineData("modal", "post", "private")]
    [InlineData("modal", "text", "private")]
    [InlineData("custom_operation", "post", "private")]
    [InlineData("custom_operation", "text", "private")]
    [InlineData("message_delete", "post", "group")]
    [InlineData("modal", "text", "group")]
    [InlineData("reaction", "post", "private")]
    public async Task ParseToRunner_UnsupportedOuterOperationWithRawSnapshot_DoesNotRequestLlmReply(
        string outerType, string rawType, string conversationType)
    {
        using var fixture = new Fixture();
        var parsed = fixture.Parse(outerType, rawType, conversationType, "snapshot text");
        var turn = parsed.Activity is null ? null : await fixture.Runner.RunInboundAsync(
            parsed.Activity, new ConversationTurnRuntimeContext(NyxRelayReplyToken: null, IsReplyToBot: true), CancellationToken.None);

        turn?.LlmReplyRequest.Should().BeNull("an original message snapshot cannot turn an unsupported operation into a new message");
        parsed.Success.Should().BeFalse();
        parsed.Ignored.Should().BeTrue();
        parsed.ErrorCode.Should().Be("unsupported_content_type");
        parsed.Activity.Should().BeNull();
    }

    [Theory]
    [InlineData("unknown", "post")]
    [InlineData("", "post")]
    [InlineData("text", null)]
    public async Task ParseToRunner_LegacyRawMessageOrOrdinaryText_RequestsPrivateLlmReply(
        string outerType, string? rawType)
    {
        using var fixture = new Fixture();
        var parsed = fixture.Parse(outerType, rawType, "private", rawType is null ? "ordinary text" : null);

        parsed.Success.Should().BeTrue();
        var turn = await fixture.Runner.RunInboundAsync(parsed.Activity!, ConversationTurnRuntimeContext.Empty, CancellationToken.None);

        turn.Success.Should().BeTrue();
        turn.LlmReplyRequest.Should().NotBeNull();
        turn.LlmReplyRequest!.Activity.Content.Text.Should().Contain(rawType is null ? "ordinary text" : "raw message body");
        turn.LlmReplyRequest.Activity.ChannelId.Value.Should().Be("lark");
        turn.LlmReplyRequest.Activity.TransportExtras.NyxAgentApiKeyId.Should().Be("key-lark");
        turn.LlmReplyRequest.RegistrationId.Should().Be("reg-lark");
    }

    [Theory]
    [InlineData("unknown", "post", false)]
    [InlineData("unknown", "post", true)]
    [InlineData("text", null, false)]
    [InlineData("text", null, true)]
    public async Task ParseToRunner_SupportedGroupMessage_PreservesAddressingRule(
        string outerType, string? rawType, bool isReplyToBot)
    {
        using var fixture = new Fixture();
        var parsed = fixture.Parse(outerType, rawType, "group", rawType is null ? "ordinary text" : null);

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        var turn = await fixture.Runner.RunInboundAsync(parsed.Activity,
            new ConversationTurnRuntimeContext(NyxRelayReplyToken: null, IsReplyToBot: isReplyToBot), CancellationToken.None);

        (turn.LlmReplyRequest is not null).Should().Be(isReplyToBot);
        if (!isReplyToBot)
            turn.SentActivityId.Should().StartWith("ignored:group_message_not_addressed");
    }

    [Theory]
    [InlineData("unknown", "post", "matrix", "key-lark", "channel_platform_mismatch")]
    [InlineData("text", null, "matrix", "key-lark", "channel_platform_mismatch")]
    [InlineData("unknown", "post", "lark", "key-unregistered", "registration_not_found")]
    [InlineData("text", null, "lark", "key-unregistered", "registration_not_found")]
    public async Task ParseToRunner_SupportedMessage_DoesNotBypassRegistrationIdentity(
        string outerType, string? rawType, string registrationPlatform, string callbackKey, string expectedError)
    {
        using var fixture = new Fixture(registrationPlatform);
        var parsed = fixture.Parse(outerType, rawType, "private", "message text", callbackKey);

        parsed.Success.Should().BeTrue();
        var turn = await fixture.Runner.RunInboundAsync(parsed.Activity!, ConversationTurnRuntimeContext.Empty, CancellationToken.None);

        turn.ErrorCode.Should().Be(expectedError);
        turn.LlmReplyRequest.Should().BeNull();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly HttpClient _http = new(new RejectNetworkHandler());
        private readonly LarkRelayMessageAdapter _adapter = new();
        public ChannelConversationTurnRunner Runner { get; }

        public Fixture(string registrationPlatform = "lark")
        {
            var secret = new SecretReference
            {
                Ref = "secret-lark", Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
                OwnerScopeKey = "scope-lark", Version = 1, Fingerprint = "sha256:lark", CreatedAtUnixMs = 1788825600000,
            };
            var registration = new ChannelBotRegistrationEntry
            {
                Id = "reg-lark", ScopeId = "scope-lark", Platform = registrationPlatform,
                NyxChannelBotId = "bot-lark", NyxAgentApiKeyId = "key-lark", NyxProviderSlug = "api-lark-bot",
                AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
                WorkflowResultDeliveryCredential = secret.Clone(),
                ChannelAgentKey = new ChannelAgentKeyCredential
                {
                    ApiKeyId = "key-lark", SecretReference = secret.Clone(),
                    Grant = new ChannelAgentKeyGrantSnapshot { AllowAllServices = true, AllowAllNodes = true },
                },
            };
            var query = Substitute.For<IChannelBotRegistrationQueryPort>();
            query.GetSnapshotAsync(registration.Id, Arg.Any<CancellationToken>())
                .Returns(new ChannelBotRegistrationSnapshot(registration, 17));
            var identityQuery = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
            identityQuery.ListByNyxAgentApiKeyIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
                    call.Arg<string>() == "key-lark" ? [registration] : []));
            var identityResolver = Substitute.For<ILarkBotIdentityResolver>();
            identityResolver.ResolveBotOpenIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>("ou_bot"));
            var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, _http);
            Runner = new ChannelConversationTurnRunner(_services, query, identityQuery, [], client,
                new NyxIdRelayOutboundPort(client, NullLogger<NyxIdRelayOutboundPort>.Instance, []), null,
                NullLogger<ChannelConversationTurnRunner>.Instance, Substitute.For<IAgentToolExecutionPort>(),
                groupAdmissionPolicies: [new LarkGroupAdmissionPolicy(identityResolver)]);
        }

        public NyxIdRelayParseResult Parse(string outerType, string? rawType, string conversationType,
            string? text, string callbackKey = "key-lark")
        {
            var rawContent = rawType == "post"
                ? "{\"content\":[[{\"tag\":\"text\",\"text\":\"raw message body\"}]]}"
                : "{\"text\":\"raw message body\"}";
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                message_id = "msg-lark", platform = "lark", agent = new { api_key_id = callbackKey },
                conversation = new { id = "route-lark", platform_id = "oc_chat", type = conversationType },
                sender = new { platform_id = "ou_sender" },
                content = new { type = outerType, text }, timestamp = "2026-09-16T00:00:00Z",
                raw_platform_data = rawType is null ? null : new
                {
                    platform = "raw-untrusted-platform", agent = new { api_key_id = "raw-untrusted-key" },
                    @event = new { message = new { message_type = rawType, content = rawContent } },
                },
            });
            return new NyxIdRelayTransport([_adapter], [_adapter], [_adapter], [_adapter]).Parse(body);
        }

        public void Dispose()
        {
            _http.Dispose();
            _services.Dispose();
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Parser-to-runner admission must not call an external service.");
    }
}
