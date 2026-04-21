using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkConversationTurnRunnerTests
{
    [Fact]
    public async Task RunContinueAsync_DirectMessageWithoutPartition_ReturnsPermanentFailure()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                partition: null,
                "dm",
                "user-open-id"),
            Payload = new MessageContent { Text = "hello" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("conversation_not_found");
        result.ErrorSummary.Should().Contain("routing target");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunContinueAsync_GroupWithoutPartition_UsesCanonicalFallback()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-2",
            CorrelationId = "corr-2",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                partition: null,
                "group",
                "oc_group_chat_1"),
            Payload = new MessageContent { Text = "hello group" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.ConversationId.Should().Be("oc_group_chat_1");
        adapter.Replies[0].Inbound.ChatType.Should().Be("group");
    }

    private static LarkConversationTurnRunner CreateRunner(
        IChannelBotRegistrationQueryPort registrationQueryPort,
        RecordingPlatformAdapter adapter)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new LarkConversationTurnRunner(
            services,
            registrationQueryPort,
            [adapter],
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://example.com" }),
            new StubReplyGenerator(),
            NullLogger<LarkConversationTurnRunner>.Instance);
    }

    private static IChannelBotRegistrationQueryPort BuildRegistrationQueryPort()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            NyxUserToken = "org-token",
            ScopeId = "scope-1",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(registration.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        return queryPort;
    }

    private sealed class RecordingPlatformAdapter : IPlatformAdapter
    {
        public string Platform => "lark";

        public List<(string ReplyText, InboundMessage Inbound)> Replies { get; } = [];

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            Replies.Add((replyText, inbound));
            return Task.FromResult(new PlatformReplyDeliveryResult(true, "ok"));
        }
    }

    private sealed class StubReplyGenerator : IConversationReplyGenerator
    {
        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct) =>
            Task.FromResult<string?>("unused");
    }
}
