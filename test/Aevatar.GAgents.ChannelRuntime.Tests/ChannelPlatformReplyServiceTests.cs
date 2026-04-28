using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelPlatformReplyServiceTests
{
    [Fact]
    public async Task DeliverAsync_UsesLatestRegistrationFromRuntimeQueryPort()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxProviderSlug = "api-lark-bot",
                NyxChannelBotId = "bot-new",
            }));

        var adapter = new StubPlatformAdapter(new PlatformReplyDeliveryResult(true, "ok"));
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);

        var result = await service.DeliverAsync(
            adapter,
            "hello",
            BuildInbound(),
            new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxChannelBotId = "bot-old",
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        adapter.Registrations.Should().ContainSingle();
        adapter.Registrations[0].NyxChannelBotId.Should().Be("bot-new");
    }

    [Fact]
    public async Task DeliverAsync_FallsBackToProvidedRegistration_WhenRuntimeQueryMisses()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        runtimeQueryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));

        var adapter = new StubPlatformAdapter(new PlatformReplyDeliveryResult(true, "ok"));
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            NyxChannelBotId = "bot-provided",
        };

        await service.DeliverAsync(
            adapter,
            "hello",
            BuildInbound(),
            registration,
            CancellationToken.None);

        adapter.Registrations.Should().ContainSingle();
        adapter.Registrations[0].NyxChannelBotId.Should().Be("bot-provided");
    }

    [Fact]
    public async Task DeliverAsync_ReturnsAdapterFailureUnchanged()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        var failure = new PlatformReplyDeliveryResult(false, "lark_direct_platform_reply_retired", PlatformReplyFailureKind.Permanent);
        var adapter = new StubPlatformAdapter(failure);
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);

        var result = await service.DeliverAsync(
            adapter,
            "hello",
            BuildInbound(),
            new ChannelBotRegistrationEntry { Id = "reg-1", Platform = "lark" },
            CancellationToken.None);

        result.Should().Be(failure);
    }

    private static InboundMessage BuildInbound() => new()
    {
        Platform = "lark",
        ConversationId = "chat-1",
        SenderId = "user-1",
        SenderName = "user-1",
        Text = "hello",
    };

    private sealed class StubPlatformAdapter(PlatformReplyDeliveryResult result) : IPlatformAdapter
    {
        public string Platform => "lark";
        public List<ChannelBotRegistrationEntry> Registrations { get; } = [];

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
            Registrations.Add(registration.Clone());
            return Task.FromResult(result);
        }
    }
}
