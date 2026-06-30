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

    [Fact]
    public async Task DeliverAsync_LarkLongReply_SplitsIntoOrderedLosslessChunks()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        var adapter = new StubPlatformAdapter(new PlatformReplyDeliveryResult(true, "ok"));
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);
        var draft1 = "**1. Warm & Polite**\n" + new string('A', 16_000);
        var draft2 = "**2. Friendly & Engaging**\n" + new string('B', 16_000);
        var draft3 = "**3. Simple & Sweet**\nThank you so much for everything this year. Wishing you all a lovely summer!";
        var reply = string.Join("\n\n", [draft1, draft2, draft3]);

        var result = await service.DeliverAsync(
            adapter,
            reply,
            BuildInbound(),
            new ChannelBotRegistrationEntry { Id = "reg-1", Platform = "lark" },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        adapter.Replies.Should().HaveCountGreaterThan(1);
        adapter.Replies.Should().OnlyContain(chunk => chunk.Length <= 30_000);
        adapter.Replies[0].Should().Contain("[part 1/");
        adapter.Replies[^1].Should().Contain($"[part {adapter.Replies.Count}/{adapter.Replies.Count} continued]");
        adapter.Replies[^1].Should().Contain("**3. Simple & Sweet**");
        adapter.Replies[^1].Should().Contain("Wishing you all a lovely summer!");
    }

    [Fact]
    public async Task DeliverAsync_NonLarkLongReply_DoesNotChunk()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        var adapter = new StubPlatformAdapter(new PlatformReplyDeliveryResult(true, "ok"), platform: "telegram");
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);
        var reply = new string('x', 40_000);

        var result = await service.DeliverAsync(
            adapter,
            reply,
            BuildInbound(),
            new ChannelBotRegistrationEntry { Id = "reg-1", Platform = "telegram" },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        adapter.Replies.Should().ContainSingle().Which.Should().Be(reply);
    }

    [Fact]
    public async Task DeliverAsync_LarkChunkFailure_ReturnsObservableChunkFailure()
    {
        var runtimeQueryPort = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        var adapter = new StubPlatformAdapter(
            new PlatformReplyDeliveryResult(true, "ok"),
            new PlatformReplyDeliveryResult(false, "lark rejected body", PlatformReplyFailureKind.Transient));
        var service = new ChannelPlatformReplyService(
            runtimeQueryPort,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<ChannelPlatformReplyService>.Instance);
        var reply = string.Join("\n\n", [new string('a', 29_000), new string('b', 29_000)]);

        var result = await service.DeliverAsync(
            adapter,
            reply,
            BuildInbound(),
            new ChannelBotRegistrationEntry { Id = "reg-1", Platform = "lark" },
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Transient);
        result.Detail.Should().Contain("chunk 2/");
        result.Detail.Should().Contain("lark rejected body");
        adapter.Replies.Should().HaveCount(2);
    }

    private static InboundMessage BuildInbound() => new()
    {
        Platform = "lark",
        ConversationId = "chat-1",
        SenderId = "user-1",
        SenderName = "user-1",
        Text = "hello",
    };

    private sealed class StubPlatformAdapter : IPlatformAdapter
    {
        private readonly Queue<PlatformReplyDeliveryResult> _results;
        private readonly string _platform;

        public StubPlatformAdapter(params PlatformReplyDeliveryResult[] results)
            : this(results, "lark")
        {
        }

        public StubPlatformAdapter(PlatformReplyDeliveryResult result, string platform)
            : this([result], platform)
        {
        }

        private StubPlatformAdapter(PlatformReplyDeliveryResult[] results, string platform)
        {
            _results = new Queue<PlatformReplyDeliveryResult>(results);
            _platform = platform;
        }

        public string Platform => _platform;
        public List<ChannelBotRegistrationEntry> Registrations { get; } = [];
        public List<string> Replies { get; } = [];

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
            Replies.Add(replyText);
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new PlatformReplyDeliveryResult(true, "ok"));
        }
    }
}
