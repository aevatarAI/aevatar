using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelLlmReplyInboxRuntimeTests
{
    [Fact]
    public async Task ProcessAsync_RelayTurnCapturesInteractiveIntentIntoReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() =>
        {
            var intent = new MessageContent
            {
                Text = "Choose one",
            };
            intent.Actions.Add(new ActionElement
            {
                Kind = ActionElementKind.Button,
                ActionId = "confirm",
                Label = "Confirm",
                IsPrimary = true,
            });
            return collector.Capture(intent);
        });
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            collector,
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        replyGenerator.CaptureSucceeded.Should().BeTrue();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("Choose one");
        ready.Outbound.Actions.Should().ContainSingle();
        ready.Outbound.Actions[0].ActionId.Should().Be("confirm");
    }

    [Fact]
    public async Task ProcessAsync_NonRelayTurnDoesNotEnableInteractiveScope()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => collector.Capture(new MessageContent { Text = "ignored" }))
        {
            ReplyText = "plain reply",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            collector,
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-2",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-2",
                Content = new MessageContent { Text = "hello" },
            },
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("plain reply");
        ready.Outbound.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ShouldEmitFailedReply_WhenGeneratorThrows()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new ThrowingReplyGenerator(new InvalidOperationException("boom"));
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            collector,
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-throw",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_failed");
        ready.ErrorSummary.Should().Be("boom");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_ShouldEmitFailedReply_WhenGeneratorReturnsEmpty()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "   ",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            collector,
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-empty",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropSilently_WhenTargetActorIdMissing()
    {
        var actorRuntime = Substitute.For<IActorRuntime>();
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-missing",
            TargetActorId = string.Empty,
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        await actorRuntime.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropSilently_WhenActivityMissing()
    {
        var actorRuntime = Substitute.For<IActorRuntime>();
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-activity",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
        });

        await actorRuntime.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
    }

    private static ChatActivity BuildRelayActivity() =>
        new()
        {
            Id = "msg-1",
            ChannelId = ChannelId.From("lark"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Content = new MessageContent { Text = "hello" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                ReplyAccessToken = "relay-token-1",
            },
        };

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : IConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public bool CaptureSucceeded { get; private set; }

        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct)
        {
            CaptureSucceeded = captureAction();
            return Task.FromResult<string?>(ReplyText);
        }
    }

    private sealed class ThrowingReplyGenerator(Exception exception) : IConversationReplyGenerator
    {
        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct) => Task.FromException<string?>(exception);
    }
}
