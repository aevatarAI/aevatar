using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Studio.Application.Studio.Abstractions;
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-throw",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-throw",
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-empty",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-empty",
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_ShouldEchoReplyTokenIntoLlmReplyReadyEvent()
    {
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
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-echo",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-echo",
            ReplyTokenExpiresAtUnixMs = expiresAtUnixMs,
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.ReplyToken.Should().Be("relay-token-echo");
        ready.ReplyTokenExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropRelayRequest_WhenInboxCarriesNoReplyToken()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        // Relay activity but no inbox-carried ReplyToken — simulates a request rehydrated
        // from persisted state after a pod restart, where the original token capture is gone.
        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-token",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-token");
        dropped.Reason.Should().Be("missing_relay_reply_token");
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropRequest_WhenOlderThanMaxAge()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        var requestedAtUnixMs = DateTimeOffset.UtcNow
            .AddMilliseconds(-(ChannelLlmReplyInboxRuntime.MaxInboxRequestAgeMs + 60_000))
            .ToUnixTimeMilliseconds();
        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale",
            RequestedAtUnixMs = requestedAtUnixMs,
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-stale");
        dropped.Reason.Should().Be("stale_inbox_request_dropped");
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
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
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
    public async Task ProcessAsync_ShouldNotifyActor_WhenActivityMissing()
    {
        // Malformed payload (no Activity) should still tell the actor to retire its
        // pending entry — the actor decides whether to clean up. Otherwise the entry
        // accumulates silently in State.PendingLlmReplyRequests until rehydration.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));
        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-activity",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
        });

        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-activity");
        dropped.Reason.Should().Be("malformed_deferred_llm_reply_request");
    }

    [Fact]
    public async Task ProcessAsync_ShouldApplyBotOwnerLlmConfig_FromUserConfigQueryPort()
    {
        // Bot owner's LLM model + route comes from UserConfig (the same store that backs
        // their nyxid-chat preferences), looked up by the scope id resolved from the
        // bot registration. The relay turn must NOT depend on the inbound user-token's
        // freshness for LLM auth, and must override server defaults with what the bot
        // owner has configured.
        var capturedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            MetadataObserver = m =>
            {
                foreach (var pair in m)
                    capturedMetadata[pair.Key] = pair.Value;
            },
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));

        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-bot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("scope-bot-owner"));

        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-bot-owner", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11)));

        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance,
            scopeResolver,
            userConfigQueryPort);

        var activity = BuildRelayActivity();
        activity.Bot = BotInstanceId.From("api-key-bot");
        activity.TransportExtras = new TransportExtras
        {
            // The 15-min user session token must NOT leak into LLM auth metadata.
            NyxUserAccessToken = "ephemeral-user-jwt-DO-NOT-USE-FOR-LLM",
        };

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bot-owner",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-bot-owner",
        });

        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.ModelOverride)
            .WhoseValue.Should().Be("gpt-4o-bot-owner");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference)
            .WhoseValue.Should().Be("/api/v1/proxy/s/anthropic-via-bot-owner");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride)
            .WhoseValue.Should().Be("11");
        capturedMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        capturedMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotLeakUserAccessTokenIntoLlmAuthMetadata()
    {
        // Regression: the previous implementation copied Activity.TransportExtras
        // .NyxUserAccessToken into LLMRequestMetadataKeys.NyxIdAccessToken, which
        // caused token_expired LLM rejections once the inbound user's NyxID session
        // (~15 min TTL) lapsed. The token must never become the LLM call's bearer.
        var capturedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            MetadataObserver = m =>
            {
                foreach (var pair in m)
                    capturedMetadata[pair.Key] = pair.Value;
            },
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("actor-1").Returns(Task.FromResult<IActor?>(actor));

        var runtime = new ChannelLlmReplyInboxRuntime(
            Substitute.For<IStreamProvider>(),
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ChannelLlmReplyInboxRuntime>.Instance);

        var activity = BuildRelayActivity();
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "ephemeral-user-jwt",
        };

        await runtime.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-leak",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-1",
        });

        capturedMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        capturedMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
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
                CorrelationId = "corr-1",
            },
        };

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : IConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public bool CaptureSucceeded { get; private set; }

        public Action<IReadOnlyDictionary<string, string>>? MetadataObserver { get; init; }

        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct)
        {
            CaptureSucceeded = captureAction();
            MetadataObserver?.Invoke(metadata);
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
