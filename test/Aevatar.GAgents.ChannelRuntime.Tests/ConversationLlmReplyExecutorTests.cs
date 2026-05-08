using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationLlmReplyExecutorTests
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
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        });

        replyGenerator.CaptureSucceeded.Should().BeTrue();
        var handled = dispatchPort.Last;
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
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
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
        var handled = dispatchPort.Last;
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
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-throw",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-throw",
        });

        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_failed");
        ready.ErrorSummary.Should().Be("boom");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_ShouldEmitTimeoutFallbackReply_WhenGeneratorHangsPastBudget()
    {
        // Without a cancellation budget on the LLM run, a tool that hangs (broken sandbox,
        // unreachable proxy upstream, slow remote SSH) would pin the executor task indefinitely
        // and Lark would stay on the loading reaction forever. The executor caps each turn at
        // the relay ResponseTimeoutSeconds and folds the cancellation into a user-visible
        // fallback reply with errorCode=llm_reply_timeout.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new HangingReplyGenerator();
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                ResponseTimeoutSeconds = 1,
            },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-timeout",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-timeout",
        });

        replyGenerator.WasCancelled.Should().BeTrue();
        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_timeout");
        ready.ErrorSummary.Should().Contain("1s budget");
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
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-empty",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-empty",
        });

        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_ShouldEchoReplyTokenIntoLlmReplyReadyEvent()
    {
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-echo",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-echo",
            ReplyTokenExpiresAtUnixMs = expiresAtUnixMs,
        });

        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.ReplyToken.Should().Be("relay-token-echo");
        ready.ReplyTokenExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropRelayRequest_WhenInboxCarriesNoReplyToken()
    {
        var dispatchPort = new RecordingDispatchPort();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        // Relay activity but no inbox-carried ReplyToken — simulates a request rehydrated
        // from persisted state after a pod restart, where the original token capture is gone.
        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-token",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-token");
        dropped.Reason.Should().Be("missing_relay_reply_token");
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropRequest_WhenOlderThanMaxAge()
    {
        var dispatchPort = new RecordingDispatchPort();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        var requestedAtUnixMs = DateTimeOffset.UtcNow
            .AddMilliseconds(-(ConversationLlmReplyExecutor.MaxRequestAgeMs + 60_000))
            .ToUnixTimeMilliseconds();
        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale",
            RequestedAtUnixMs = requestedAtUnixMs,
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-stale");
        dropped.Reason.Should().Be("stale_inbox_request_dropped");
    }

    [Fact]
    public async Task ProcessAsync_ShouldDropSilently_WhenTargetActorIdMissing()
    {
        // A malformed payload with an empty TargetActorId has nowhere to send the drop
        // notification — there is no actor to retire a pending entry on. The executor
        // must short-circuit cleanly without attempting to dispatch.
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-missing",
            TargetActorId = string.Empty,
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotifyActor_WhenActivityMissing()
    {
        // Malformed payload (no Activity) should still tell the actor to retire its
        // pending entry — the actor decides whether to clean up. Otherwise the entry
        // accumulates silently in State.PendingLlmReplyRequests until rehydration.
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-activity",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
        });

        var handled = dispatchPort.Last;
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-activity");
        dropped.Reason.Should().Be("malformed_deferred_llm_reply_request");
    }

    [Fact]
    public async Task ProcessAsync_StreamingEnabled_DispatchesChunkEventAndReadyEvent()
    {
        // Pin the legacy edit-message path explicitly: card-mode is now the default
        // (StreamingCardKitEnabled=true) and emits a structurally distinct
        // LlmReplyCardStreamChunkEvent. This test specifically exercises the
        // text-edit chunk shape, so opt out of card mode here.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "streamed reply" };
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingFlushIntervalMs = 0,
                StreamingCardKitEnabled = false,
            },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stream",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        dispatchPort.Dispatched.Any(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor)).Should().BeTrue();
        dispatchPort.Dispatched.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = dispatchPort.Dispatched.First(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("streamed reply");
        chunk.CorrelationId.Should().Be("corr-stream");
    }

    [Fact]
    public async Task ProcessAsync_StreamingEnabledWithDefaultCardMode_DispatchesCardChunkEvent()
    {
        // Pinning the new default: StreamingCardKitEnabled=true causes the sink to emit
        // the card-mode chunk type, exercising the CardKit lifecycle entrypoint without
        // needing a real ChannelCardConversationTurnRunner wired up (the actor is mocked,
        // so we only verify the executor dispatched the right proto type to the actor).
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "card streamed reply" };
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingCardKitFlushIntervalMs = 0,
                // StreamingCardKitEnabled defaults to true.
            },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-card-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-card-stream",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        dispatchPort.Dispatched.Any(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor)).Should().BeTrue();
        dispatchPort.Dispatched.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = dispatchPort.Dispatched.First(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyCardStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("card streamed reply");
        chunk.CorrelationId.Should().Be("corr-card-stream");
    }

    [Fact]
    public async Task ProcessAsync_StreamingDisabledFlag_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = false },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-legacy",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-legacy",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        dispatchPort.Dispatched.Should().ContainSingle();
        dispatchPort.Dispatched[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_StreamingEnabledButNonRelay_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-nonrelay",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-nonrelay",
                Content = new MessageContent { Text = "hello" },
                // No OutboundDelivery → not a relay turn
            },
        });

        dispatchPort.Dispatched.Should().ContainSingle();
        dispatchPort.Dispatched[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_ShouldApplyBotOwnerLlmConfig_FromUserConfigQueryPort()
    {
        // Bot owner's LLM model + route comes from UserConfig (the same store that backs
        // their nyxid-chat preferences), looked up by the scope id resolved from the
        // bot registration. The relay turn uses the inbound user-token as the bearer
        // (it is the bot owner's own NyxID session, freshly issued per callback) while
        // taking model / route / max-tool-rounds from the owner's pre-configured
        // UserConfig.
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
        var dispatchPort = new RecordingDispatchPort();

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

        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance,
            scopeResolver,
            userConfigQueryPort);

        var activity = BuildRelayActivity();
        activity.Bot = BotInstanceId.From("api-key-bot");
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await executor.ProcessAsync(new NeedsLlmReplyEvent
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
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdAccessToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdOrgToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
    }

    [Fact]
    public async Task ProcessAsync_ShouldThreadBotOwnerSessionTokenAsLlmBearer()
    {
        // The inbound X-NyxID-User-Token is the bot owner's own NyxID session JWT.
        // It is the credential that would authorize the owner's LLM calls in
        // nyxid-chat, so it is also the correct credential for the bot's relay
        // LLM call. The stale-pending GC plus the direct-enqueue + inbox-echoed
        // token flow keeps it fresh through the window where the LLM call actually
        // fires.
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
        var dispatchPort = new RecordingDispatchPort();

        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        var activity = BuildRelayActivity();
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await executor.ProcessAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bearer",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-1",
        });

        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdAccessToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdOrgToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
    }

    [Fact]
    public async Task StartAsync_ReturnsImmediately_WhileLlmCallStillRunning()
    {
        // Non-blocking guarantee: the actor turn must not wait on the 60-300s LLM call.
        // StartAsync schedules the work on the thread pool and returns; verify by gating
        // the reply generator on a TCS that we never signal during the assertion.
        var releaseGenerator = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyGenerator = new GatedReplyGenerator(generatorEntered, releaseGenerator);
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        var startTask = executor.StartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-nonblocking",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        }, CancellationToken.None);

        // StartAsync must complete without waiting for the generator to finish.
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));
        startTask.IsCompletedSuccessfully.Should().BeTrue();

        // Confirm the background task actually started the generator (so we know we're
        // testing "non-blocking despite work in progress" rather than "no work happened").
        await generatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatchPort.Dispatched.Should().BeEmpty(
            "the executor must not dispatch a terminal signal until the generator returns");

        // Drain: release the generator and let the background task finalize.
        releaseGenerator.SetResult();
    }

    [Fact]
    public async Task StartAsync_ClonesRequest_SoCallerMutationsDoNotAffectInFlightWork()
    {
        // Snapshot guarantee: StartAsync must not pin the caller's NeedsLlmReplyEvent —
        // the actor often clones+mutates pending requests on retry/rehydration. The
        // background task should observe the values at the moment of StartAsync.
        var observedActivities = new List<ChatActivity>();
        var releaseGenerator = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyGenerator = new GatedReplyGenerator(generatorEntered, releaseGenerator)
        {
            ReplyText = "ok",
            ActivityObserver = activity => observedActivities.Add(activity.Clone()),
        };
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-snapshot",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        };
        await executor.StartAsync(request, CancellationToken.None);

        await generatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Mutate the original AFTER StartAsync — the in-flight task must observe the
        // pre-mutation activity.
        request.Activity!.Content = new MessageContent { Text = "MUTATED-AFTER-START" };
        request.ReplyToken = "MUTATED-TOKEN";

        releaseGenerator.SetResult();

        // Wait for terminal dispatch to confirm the work finished.
        await WaitForDispatchAsync(dispatchPort, count: 1, TimeSpan.FromSeconds(5));

        observedActivities.Should().ContainSingle();
        observedActivities[0].Content.Text.Should().Be("hello",
            "the generator must observe the cloned activity, not post-StartAsync mutations");
    }

    [Fact]
    public async Task StartAsync_DispatchesExecutorCrashDrop_WhenBackgroundTaskThrowsAfterGates()
    {
        // Contract: the executor MUST eventually deliver a terminal signal so the actor
        // can retire its pending entry. If something past the pre-LLM gates throws
        // (here: the dispatch of the ready event itself fails), the outer catch must
        // send DeferredLlmReplyDroppedEvent with reason executor_crash.
        //
        // Streaming is disabled here so the only dispatch is the ready event — that way
        // the first ThrowOnceDispatchPort attempt is unambiguously the ready dispatch
        // (with streaming on, the streaming sink dispatches first and the sink swallows
        // its own dispatch errors, masking the failure path under test).
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var dispatchPort = new ThrowOnceDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        await executor.StartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-crash",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-crash",
        }, CancellationToken.None);

        // First dispatch (LlmReplyReadyEvent) throws; the outer catch sends a second
        // dispatch (DeferredLlmReplyDroppedEvent) so the actor isn't left with a leaked
        // pending entry.
        await WaitForDispatchAsync(dispatchPort, count: 2, TimeSpan.FromSeconds(5));

        dispatchPort.Attempts[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
        var dropped = dispatchPort.Attempts[1].Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-crash");
        dropped.Reason.Should().Be("executor_crash");
    }

    [Fact]
    public async Task StartAsync_ThrowsImmediately_WhenCallerTokenAlreadyCancelled()
    {
        // A turn that's already cancelled before reaching the executor shouldn't burn
        // an LLM round; throw directly so the actor sees the cancellation and skips
        // dispatch instead of swallowing it inside a background task.
        var dispatchPort = new RecordingDispatchPort();
        var executor = new ConversationLlmReplyExecutor(
            dispatchPort,
            new RecordingReplyGenerator(() => false) { ReplyText = "should not run" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            NullLogger<ConversationLlmReplyExecutor>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await executor.StartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cancelled",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    private static async Task WaitForDispatchAsync(
        RecordingDispatchPort port,
        int count,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (port.Dispatched.Count < count && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        port.Dispatched.Count.Should().BeGreaterThanOrEqualTo(count);
    }

    private static async Task WaitForDispatchAsync(
        ThrowOnceDispatchPort port,
        int count,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (port.Attempts.Count < count && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        port.Attempts.Count.Should().BeGreaterThanOrEqualTo(count);
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

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Dispatched { get; } = [];

        public EventEnvelope? Last => Dispatched.Count == 0 ? null : Dispatched[^1];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add(envelope);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws on the first dispatch and accepts the rest. Lets the executor-crash test
    /// confirm that the outer catch sends a follow-up DeferredLlmReplyDroppedEvent after
    /// the first envelope (the ready signal) fails to land.
    /// </summary>
    private sealed class ThrowOnceDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Attempts { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Attempts.Add(envelope);
            if (Attempts.Count == 1)
                throw new InvalidOperationException("dispatch failure simulation");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : IConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public bool CaptureSucceeded { get; private set; }

        public Action<IReadOnlyDictionary<string, string>>? MetadataObserver { get; init; }

        public async Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            CaptureSucceeded = captureAction();
            MetadataObserver?.Invoke(metadata);
            if (streamingSink is not null && !string.IsNullOrEmpty(ReplyText))
                await streamingSink.OnDeltaAsync(ReplyText, ct);
            return ReplyText;
        }
    }

    private sealed class ThrowingReplyGenerator(Exception exception) : IConversationReplyGenerator
    {
        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => Task.FromException<string?>(exception);
    }

    /// <summary>
    /// Generator that signals when invoked (<paramref name="entered"/>) and waits for an
    /// external release (<paramref name="release"/>) before returning. Used to assert
    /// non-blocking semantics (StartAsync returns while the generator is still running)
    /// and snapshot semantics (the generator observes the cloned activity, not later
    /// caller mutations).
    /// </summary>
    private sealed class GatedReplyGenerator(
        TaskCompletionSource entered,
        TaskCompletionSource release) : IConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public Action<ChatActivity>? ActivityObserver { get; init; }

        public async Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            ActivityObserver?.Invoke(activity);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ReplyText;
        }
    }

    /// <summary>Generator that never completes on its own; only ends when the executor cancels it.</summary>
    private sealed class HangingReplyGenerator : IConversationReplyGenerator
    {
        public bool WasCancelled { get; private set; }

        public async Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            var pendingReply = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = ct.Register(() =>
            {
                WasCancelled = true;
                pendingReply.TrySetCanceled(ct);
            });

            return await pendingReply.Task;
        }
    }
}
