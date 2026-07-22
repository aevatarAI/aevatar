using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using AGUIEvent = Aevatar.AGUI.Contracts.AGUIEvent;

namespace Aevatar.AI.Tests;

public class NyxIdChatGAgentTests
{
    [Fact]
    public void StoredChatMessage_ShouldExposeTypedTurnIdentity()
    {
        typeof(StoredChatMessage).GetProperty("TurnId").Should().NotBeNull();
    }

    [Fact]
    public async Task ActivateAsync_ShouldPinNyxIdProviderOnFirstInitialization()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var agent = CreateAgent(provider, "nyxid-chat-init");

        await agent.ActivateAsync();

        agent.RoleName.Should().Be(NyxIdChatServiceDefaults.DisplayName);
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        agent.EffectiveConfig.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
    }

    [Fact]
    public async Task ActivateAsync_ShouldMigrateLegacyBlankProviderToNyxId()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var actorId = "nyxid-chat-migration";

        var legacyAgent = CreateAgent(provider, actorId);
        await legacyAgent.ActivateAsync();
        await legacyAgent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = NyxIdChatServiceDefaults.DisplayName,
            ProviderName = string.Empty,
            Model = "claude-sonnet",
            SystemPrompt = "legacy prompt",
            MaxToolRounds = 7,
        });
        await legacyAgent.DeactivateAsync();

        var migratedAgent = CreateAgent(provider, actorId);
        await migratedAgent.ActivateAsync();

        migratedAgent.State.ConfigOverrides.Should().NotBeNull();
        migratedAgent.State.ConfigOverrides.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        migratedAgent.State.ConfigOverrides.Model.Should().Be("claude-sonnet");
        migratedAgent.State.ConfigOverrides.MaxToolRounds.Should().Be(7);
        migratedAgent.EffectiveConfig.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        migratedAgent.EffectiveConfig.Model.Should().Be("claude-sonnet");
        migratedAgent.EffectiveConfig.MaxToolRounds.Should().Be(7);
        migratedAgent.EffectiveConfig.SystemPrompt.Should().NotBe("legacy prompt");
        migratedAgent.EffectiveConfig.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleChatRequest_ShouldContinueToolLoopAndPublishToolLifecycleEvents()
    {
        // ─── Test fixture constants (single source of truth) ───
        const string round1Text = "Confirmed the connector.";
        const string round2Text = "Telegram Bot connection is ready.";
        const string toolCallId = "catalog-call-1";
        const string toolName = "nyxid_catalog";
        const string toolArgs = """{"action":"show","slug":"telegram-bot"}""";
        const string toolResult = """{"slug":"telegram-bot","provider_type":"api_key"}""";

        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = round1Text },
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = toolCallId,
                            Name = toolName,
                            ArgumentsJson = toolArgs,
                        },
                    },
                ],
                [
                    new LLMStreamChunk { DeltaContent = round2Text },
                ],
            ]);
        var toolSources = new IAgentToolSource[]
        {
            new StaticToolSource(
            [
                new DelegateTool(toolName, _ => toolResult),
            ]),
        };
        var agent = CreateAgent(provider, "nyxid-chat-tool-loop", llmProviderFactory, toolSources);
        var eventPublisher = new RecordingEventPublisher();
        agent.EventPublisher = eventPublisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Connect the Telegram bot",
            SessionId = "session-tool-loop",
        });

        // ─── LLM round assertions ───

        // Two LLM rounds: initial + continuation after tool result
        llmProviderFactory.StreamRequests.Should().HaveCount(2,
            "tool call in round 1 should trigger a second LLM round");

        // Round-2 messages must carry the tool result from round 1
        llmProviderFactory.StreamRequests[1].Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == toolCallId &&
            message.Content == toolResult);

        // ─── Tool lifecycle events ───

        eventPublisher.Published.OfType<ToolCallEvent>()
            .Should()
            .ContainSingle(x =>
                x.CallId == toolCallId &&
                x.ToolName == toolName &&
                x.ArgumentsJson.Contains("telegram-bot"));
        eventPublisher.Published.OfType<ToolResultEvent>()
            .Should()
            .ContainSingle(x =>
                x.CallId == toolCallId &&
                x.Success &&
                x.ResultJson.Contains("telegram-bot"));

        // ─── Streaming content events ───

        // RoleGAgent keeps the core ChatRuntime stream transparent by default; the
        // Lark/NyxId deferred reply path opts into hiding tool-call preamble text.
        llmProviderFactory.StreamRequests[1].Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Content == round1Text &&
            message.ToolCalls != null &&
            message.ToolCalls.Count == 1 &&
            message.ToolCalls[0].Id == toolCallId);
        var deltas = eventPublisher.Published.OfType<TextMessageContentEvent>()
            .Select(x => x.Delta).ToList();
        deltas.Should().ContainInOrder(round1Text, round2Text);

        // ─── Completion event ───

        var endEvent = eventPublisher.Published.OfType<TextMessageEndEvent>()
            .Should().ContainSingle().Subject;
        endEvent.Content.Should().StartWith(round1Text);
        endEvent.Content.Should().EndWith(round2Text);
        var middle = endEvent.Content[round1Text.Length..^round2Text.Length];
        middle.Should().MatchRegex(@"^\s*$",
            "only whitespace separators allowed between round-1 and round-2 text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedProjectionPipeline_ShouldFlushLiveTextAndSnapshotEveryToolProtocol(
        bool emitTextToolCall)
    {
        const string actorId = "nyxid-chat-live-progress";
        const string sessionId = "turn-live-progress";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var provider = new ControlledProgressProviderFactory(emitTextToolCall);
        var tool = new ControlledProgressTool();
        var agent = CreateAgent(
            services,
            actorId,
            provider,
            [new StaticToolSource([tool])]);

        var streams = new InMemoryStreamProvider();
        var actorPublisher = new LocalActorPublisher(actorId, static () => null, static () => 0, streams);
        agent.EventPublisher = actorPublisher;
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, actorPublisher);

        await using var responseBody = new FlushedSseFrameStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        var sseWriter = new NyxIdChatSseWriter(httpContext.Response);
        var aguiHub = new ProjectionSessionEventHub<AGUIEvent>(
            streams,
            new NyxIdChatSessionEventCodec());
        var projectionContext = new NyxIdChatSessionProjectionContext
        {
            RootActorId = actorId,
            SessionId = sessionId,
            ProjectionKind = "nyxid-chat-session",
        };
        var projector = new NyxIdChatSessionEventProjector(aguiHub);
        var committedPayloads = new List<Any>();

        await using var aguiSubscription = await aguiHub.SubscribeAsync(
            actorId,
            sessionId,
            async evt =>
            {
                _ = await NyxIdChatAguiSseEventWriter.WriteAsync(
                    evt,
                    sessionId,
                    sseWriter,
                    timeout.Token);
            },
            timeout.Token);
        await using var committedSubscription = await streams.GetStream(actorId).SubscribeAsync<EventEnvelope>(
            async envelope =>
            {
                if (CommittedStateEventEnvelope.TryGetObservedPayload(
                        envelope,
                        out var payload,
                        out _,
                        out _) && payload != null)
                {
                    committedPayloads.Add(payload.Clone());
                }

                await projector.ProjectAsync(projectionContext, envelope, timeout.Token);
            },
            timeout.Token);

        await agent.ActivateAsync(timeout.Token);
        var turnTask = agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Use the controlled tool and answer.",
            SessionId = sessionId,
        });

        await provider.WaitingForFirstRoundRelease.Task.WaitAsync(timeout.Token);
        var observedFrames = new List<JsonObject>();
        var firstContent = await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "TEXT_MESSAGE_CONTENT",
            timeout.Token);

        firstContent["textMessageContent"]!["delta"]!.GetValue<string>().Should().Be("first chunk");
        firstContent["sequence"]!.GetValue<long>().Should().BeGreaterThan(0);
        provider.FirstRoundReleased.Should().BeFalse();
        turnTask.IsCompleted.Should().BeFalse();

        provider.ReleaseFirstRound();
        var toolStart = await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "TOOL_CALL_START",
            timeout.Token);
        await tool.Started.Task.WaitAsync(timeout.Token);

        toolStart["toolCallStart"]!["toolName"]!.GetValue<string>().Should().Be(tool.Name);
        toolStart["toolCallStart"]!["presentation"]!["displayName"]!
            .GetValue<string>().Should().Be("Controlled lookup");
        tool.Released.Should().BeFalse();
        turnTask.IsCompleted.Should().BeFalse();

        tool.Release("{\"ok\":true}");
        await turnTask.WaitAsync(timeout.Token);
        await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "RUN_FINISHED",
            timeout.Token);

        var frameTypes = observedFrames.Select(FrameType).ToArray();
        frameTypes.Should().ContainInOrder(
            "TEXT_MESSAGE_START",
            "TEXT_MESSAGE_CONTENT",
            "TOOL_CALL_START",
            "TOOL_CALL_END",
            "TEXT_MESSAGE_CONTENT",
            "USAGE",
            "TEXT_MESSAGE_END",
            "RUN_FINISHED");
        frameTypes.Should().ContainSingle(type => type == "RUN_FINISHED");
        frameTypes.Should().NotContain("RUN_ERROR");

        var sequences = observedFrames
            .Select(frame => frame["sequence"]!.GetValue<long>())
            .ToArray();
        sequences.Should().BeInAscendingOrder();
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(static value => (long)value));

        var completionIndex = committedPayloads.FindIndex(payload =>
            payload.Is(RoleChatSessionCompletedEvent.Descriptor));
        completionIndex.Should().BeGreaterThanOrEqualTo(0);
        var completion = committedPayloads[completionIndex].Unpack<RoleChatSessionCompletedEvent>();
        completion.TerminalProgress.Should().ContainSingle(progress =>
            progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        committedPayloads.Should().NotContain(payload =>
            payload.Is(RoleChatSessionProgressedEvent.Descriptor) &&
            payload.Unpack<RoleChatSessionProgressedEvent>().PayloadCase ==
            RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        completion.ToolCalls.Should().ContainSingle();
        completion.ToolCalls[0].ToolName.Should().Be(tool.Name);
        completion.ToolCalls[0].Presentation.DisplayName.Should().Be("Controlled lookup");
        completion.ToolCalls[0].Presentation.BuiltIn.ToolId.Should().Be(tool.Name);
    }

    [Fact]
    public async Task ActivateAsync_ShouldUseConfiguredRelayCallbackUrlInSystemPrompt()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = "ok" },
                ],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-relay-prompt",
            llmProviderFactory,
            relayOptions: new NyxIdRelayOptions
            {
                WebhookBaseUrl = "https://dev.aevatar.local/",
            });

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "relay-prompt-session",
        });

        llmProviderFactory.StreamRequests.Should().ContainSingle();
        var systemPrompt = llmProviderFactory.StreamRequests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
        systemPrompt.Should().Contain("do not call `lark_messages_reply` or `lark_messages_react` to deliver the answer");
        systemPrompt.Should().Contain("the channel runtime will send it through the Nyx relay reply token");
        systemPrompt.Should().NotContain("call `lark_messages_react` first");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldSaveDirectChatTurnToHistory()
    {
        var history = new RecordingChatHistoryCommandPort();
        var now = DateTimeOffset.Parse("2026-06-11T01:02:03Z", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaContent = "direct answer",
                        Usage = new TokenUsage(3, 5, 8),
                    },
                ],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-history",
            llmProviderFactory,
            timeProvider: new FixedTimeProvider(now));

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "How do I connect a bot?",
            SessionId = "session-history",
        });

        history.Saved.Should().ContainSingle();
        var saved = history.Saved.Single();
        saved.ScopeId.Should().Be("scope-a");
        saved.ConversationId.Should().Be("nyxid-chat-history");
        saved.Meta.Should().BeEquivalentTo(new ConversationMeta(
            "nyxid-chat-history",
            "How do I connect a bot?",
            "nyxid-chat-history",
            NyxIdChatServiceDefaults.GAgentKind,
            now,
            now,
            2,
            NyxIdChatServiceDefaults.ProviderName,
            null));
        saved.Messages.Should().HaveCount(2);
        saved.Messages[0].Should().BeEquivalentTo(new StoredChatMessage(
            "session-history-user",
            "user",
            "How do I connect a bot?",
            now.ToUnixTimeMilliseconds(),
            "completed",
            null,
            null,
            null,
            null,
            "session-history"));
        saved.Messages[1].Should().BeEquivalentTo(new StoredChatMessage(
            "session-history-assistant",
            "assistant",
            "direct answer",
            now.ToUnixTimeMilliseconds(),
            "completed",
            null,
            null,
            null,
            null,
            "session-history"));
    }

    [Fact]
    public async Task HandleChatRequest_DifferentTurnsOnSameActor_ShouldShareHistoryAndArchiveTurnIds()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk { DeltaContent = "first answer" }],
                [new LLMStreamChunk { DeltaContent = "second answer" }],
            ]);
        var agent = CreateAgent(provider, "nyxid-chat-multi-turn", llmProviderFactory);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "first prompt",
            SessionId = "turn-first",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "second prompt",
            SessionId = "turn-second",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        llmProviderFactory.StreamRequests[1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "first answer"),
                ("user", "second prompt"));
        agent.State.Sessions["turn-first"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        agent.State.Sessions["turn-second"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);

        history.Saved.Should().HaveCount(2);
        history.Saved[0].ConversationId.Should().Be("nyxid-chat-multi-turn");
        history.Saved[1].ConversationId.Should().Be("nyxid-chat-multi-turn");
        history.Saved[0].Messages.Select(static message => message.Id)
            .Should().Equal("turn-first-user", "turn-first-assistant");
        history.Saved[1].Messages.Select(static message => message.Id)
            .Should().Equal("turn-second-user", "turn-second-assistant");
        history.Saved[0].Messages.Should().OnlyContain(static message => message.TurnId == "turn-first");
        history.Saved[1].Messages.Should().OnlyContain(static message => message.TurnId == "turn-second");
    }

    [Fact]
    public async Task HandleChatRequest_ReplayedTurn_ShouldReuseTerminalHistoryAndContinueLaterTurn()
    {
        var history = new RecordingChatHistoryCommandPort();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-20T01:02:03Z"));
        using var services = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-once",
                            Name = "count_once",
                            ArgumentsJson = "{}",
                        },
                    },
                ],
                [new LLMStreamChunk { DeltaContent = "first answer" }],
                [new LLMStreamChunk { DeltaContent = "later answer" }],
            ]);
        var toolCallCount = 0;
        var agent = CreateAgent(
            services,
            "nyxid-chat-idempotent-history",
            llmProviderFactory,
            [new StaticToolSource([new DelegateTool("count_once", _ =>
            {
                toolCallCount++;
                return "ok";
            })])],
            timeProvider: clock);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        var replayedRequest = new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "first prompt",
            SessionId = "turn-client-request-1",
        };
        await agent.HandleChatRequest(replayedRequest);
        var providerCallsAfterFirstTurn = llmProviderFactory.StreamRequests.Count;
        clock.Advance(TimeSpan.FromMinutes(1));

        await agent.HandleChatRequest(replayedRequest.Clone());

        llmProviderFactory.StreamRequests.Should().HaveCount(providerCallsAfterFirstTurn);
        toolCallCount.Should().Be(1);
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should().HaveCount(2)
            .And.OnlyContain(evt => evt.SessionId == "turn-client-request-1");
        history.Saved.Should().HaveCount(2);
        history.Saved[0].Messages.Select(static message => message.Timestamp)
            .Should().Equal(history.Saved[1].Messages.Select(static message => message.Timestamp));
        history.Saved[0].Messages.Should().OnlyContain(static message => message.TurnId == "turn-client-request-1");
        history.Saved[1].Messages.Should().OnlyContain(static message => message.TurnId == "turn-client-request-1");

        clock.Advance(TimeSpan.FromMinutes(1));
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "later prompt",
            SessionId = "turn-client-request-2",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(providerCallsAfterFirstTurn + 1);
        llmProviderFactory.StreamRequests[^1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should().ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "first answer"),
                ("user", "later prompt"));
        toolCallCount.Should().Be(1);
        agent.State.MessageCount.Should().Be(2);
        history.Saved.Should().HaveCount(3);
        history.Saved[^1].Messages.Should().OnlyContain(static message => message.TurnId == "turn-client-request-2");
    }

    [Fact]
    public async Task HandleChatRequest_DisconnectedService_ShouldArchiveBlockerAndAdmitNextTurn()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-auth",
                            Name = "nyxid_require_service",
                            ArgumentsJson =
                                """{"service_slug":"api-github","resource_uri":"/repos/private?access_token=query-secret#credential=fragment-secret"}""",
                        },
                    },
                ],
                [new LLMStreamChunk { DeltaContent = "follow-up answer" }],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-blocked-history",
            llmProviderFactory,
            [new StaticToolSource([new VerifiedMissingServiceTool()])]);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-blocked",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "ordinary follow-up",
            SessionId = "turn-after-block",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        llmProviderFactory.StreamRequests[1].Messages.Should().Contain(message =>
            message.Role == "user" && message.Content == "read private repository");
        var replayedToolMessages = llmProviderFactory.StreamRequests[1].Messages;
        var replayedAssistant = replayedToolMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        replayedAssistant.ToolCalls![0].Id.Should().Be("call-auth");
        replayedAssistant.ToolCalls[0].Name.Should().Be("nyxid_require_service");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("fragment-secret");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        replayedToolMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-auth");
        replayedToolMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("fragment-secret", StringComparison.Ordinal));
        agent.State.Sessions["turn-blocked"].Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        agent.State.Sessions["turn-blocked"].ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-auth" &&
            call.ToolName == "nyxid_require_service" &&
            call.ArgumentsJson == string.Empty);
        agent.State.Sessions["turn-blocked"].ToolReceipts
            .Should()
            .OnlyContain(receipt =>
                !receipt.ToString().Contains("query-secret", StringComparison.Ordinal) &&
                !receipt.ToString().Contains("fragment-secret", StringComparison.Ordinal));
        agent.State.Sessions["turn-after-block"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);

        history.Saved.Should().HaveCount(2);
        var blockedAssistant = history.Saved[0].Messages.Should()
            .ContainSingle(static message => message.Role == "assistant").Which;
        blockedAssistant.Id.Should().Be("turn-blocked-assistant");
        blockedAssistant.Status.Should().Be("blocked");
        blockedAssistant.Error.Should().Be(
            "No caller-visible NyxID UserService matches the requested service.");
        blockedAssistant.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
        history.Saved[1].Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Status == "completed" &&
            message.Content == "follow-up answer");
    }

    [Fact]
    public async Task HandleChatRequest_NyxId401_ShouldCommitAndProjectCredentialFreeAuthorizationBlocker()
    {
        const string actorId = "nyxid-chat-real-unauthorized";
        var eventStore = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: eventStore);
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new FixedNyxIdResponseHandler(
                HttpStatusCode.Unauthorized,
                """{"error":"unauthorized","error_code":1001,"message":"expired bearer-secret"}""")));
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-unauthorized",
                        Name = "nyxid_proxy",
                        ArgumentsJson =
                            """{"service_id":"us-github-alpha","slug":"api-github","path":"/repos/private?access_token=query-secret","headers":{"X-Credential":"header-secret"}}""",
                    },
                }],
                [new LLMStreamChunk { DeltaContent = "later answer" }],
            ]);
        var agent = CreateAgent(
            services,
            actorId,
            llmProviderFactory,
            [new StaticToolSource([new NyxIdProxyTool(client)])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-real-unauthorized",
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = "request-token-secret" },
        });

        var completed = (await eventStore.GetEventsAsync(actorId))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        completed.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        completed.AuthorizationRequired.ResourceUri.Should().Be("/repos/private");
        completed.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.Status == AgentToolReceiptStatus.AuthorizationRequired);
        completed.ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-unauthorized" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        completed.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("query-secret")
            .And.NotContain("header-secret")
            .And.NotContain("request-token-secret")
            .And.NotContain("access_token");
        publisher.Published.OfType<ToolCallEvent>().Should().ContainSingle().Which.Should().Match<ToolCallEvent>(call =>
            call.CallId == "call-unauthorized" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        publisher.Published.OfType<ToolResultEvent>().Should().ContainSingle().Which.ToString()
            .Should().NotContain("bearer-secret").And.NotContain("query-secret").And.NotContain("header-secret");
        var frames = NyxIdChatCompletionAguiFrameBuilder.Build(
            new NyxIdChatSessionProjectionContext
            {
                RootActorId = actorId,
                SessionId = completed.SessionId,
                ProjectionKind = "nyxid-chat-session",
            },
            completed);
        frames.Any(frame => frame.Custom != null && frame.Custom.Name == "nyxid.authorization.required")
            .Should().BeTrue();
        frames.Any(frame => frame.RunFinished != null &&
                            frame.RunFinished.Status == Aevatar.AGUI.Contracts.RunCompletionStatus.Blocked)
            .Should().BeTrue();
        frames.Select(frame => frame.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));
        history.Saved.Should().ContainSingle();
        history.Saved.Single().Messages.Select(message => message.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "ordinary follow-up",
            SessionId = "turn-after-unauthorized",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        var laterRequestMessages = llmProviderFactory.StreamRequests[1].Messages;
        var replayedAssistant = laterRequestMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        replayedAssistant.ToolCalls![0].Id.Should().Be("call-unauthorized");
        replayedAssistant.ToolCalls[0].Name.Should().Be("nyxid_proxy");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        laterRequestMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-unauthorized");
        laterRequestMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("header-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleChatRequest_NyxId403_ShouldRemainNormalTypedToolFailure()
    {
        const string actorId = "nyxid-chat-real-forbidden";
        var eventStore = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: eventStore);
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new FixedNyxIdResponseHandler(
                HttpStatusCode.Forbidden,
                """{"error":"forbidden","error_code":1002,"message":"approval timed out bearer-secret"}""")));
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-forbidden",
                        Name = "nyxid_proxy",
                        ArgumentsJson =
                            """{"service_id":"us-github-alpha","slug":"api-github","path":"/repos/private?access_token=query-secret","headers":{"X-Credential":"header-secret"}}""",
                    },
                }],
                [new LLMStreamChunk { DeltaContent = "The service request was denied." }],
            ]);
        var agent = CreateAgent(
            services,
            actorId,
            llmProviderFactory,
            [new StaticToolSource([new NyxIdProxyTool(client)])]);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-real-forbidden",
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = "request-token-secret" },
        });

        var completed = (await eventStore.GetEventsAsync(actorId))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        completed.AuthorizationRequired.Should().BeNull();
        completed.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.Status == AgentToolReceiptStatus.Error &&
            receipt.ErrorCode == "NYXID_PROXY_FORBIDDEN");
        completed.ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-forbidden" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        completed.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("query-secret")
            .And.NotContain("header-secret")
            .And.NotContain("request-token-secret");
        var frames = NyxIdChatCompletionAguiFrameBuilder.Build(
                new NyxIdChatSessionProjectionContext
                {
                    RootActorId = actorId,
                    SessionId = completed.SessionId,
                    ProjectionKind = "nyxid-chat-session",
                },
                completed);
        frames.Any(frame => frame.Custom != null && frame.Custom.Name == "nyxid.authorization.required")
            .Should().BeFalse();
        frames.Select(frame => frame.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));
        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        var immediateFollowUpMessages = llmProviderFactory.StreamRequests[1].Messages;
        var failedAssistant = immediateFollowUpMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        failedAssistant.ToolCalls![0].Id.Should().Be("call-forbidden");
        failedAssistant.ToolCalls[0].Name.Should().Be("nyxid_proxy");
        failedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        failedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        immediateFollowUpMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-forbidden");
        immediateFollowUpMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("header-secret", StringComparison.Ordinal));
        history.Saved.Should().ContainSingle();
        history.Saved.Single().Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Status == "completed" &&
            message.Content == "The service request was denied.");
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderFails_ShouldArchiveOnlySafeFailureMessage()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-safe-failure-history",
            new ThrowingStreamingProviderFactory(
                new InvalidOperationException("provider failed with bearer-secret credential")));

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "hello",
            SessionId = "turn-failed",
        });

        var assistant = history.Saved.Should().ContainSingle().Which.Messages
            .Should().ContainSingle(static message => message.Role == "assistant").Which;
        assistant.Status.Should().Be("error");
        assistant.Content.Should().Be("The chat request failed. Please try again.");
        assistant.Error.Should().Be("The chat request failed. Please try again.");
        assistant.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldNotSaveHistoryWithoutScopeId()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = "direct answer" },
                ],
            ]);
        var agent = CreateAgent(provider, "nyxid-chat-no-scope", llmProviderFactory);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-no-scope",
        });

        history.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenForwardedPrefixedActorRegistrationUnavailable_ShouldNotDestroyActor()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        var actorId = $"{NyxIdChatServiceDefaults.ActorIdPrefix}-existing";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = false,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
            actorId));
        runtime.DestroyedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenLocalActorRegistrationUnavailable_ShouldDestroyActor()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        const string actorId = "routed-id-without-local-prefix";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
            actorId));
        runtime.DestroyedActors.Should().ContainSingle().Which.Should().Be(actorId);
    }

    [Fact]
    public async Task HandleDeletionCompensationAsync_ShouldRestoreRegistryRegistration()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        const string actorId = "nyxid-chat-delete-compensation";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationDeletionCompensationRequested
        {
            ScopeId = "scope-a",
            ActorId = actorId,
            Reason = "history_delete_failed",
        }));

        registry.RegisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
            actorId));
    }

    private static ServiceProvider BuildServiceProvider(
        IGAgentActorRegistryCommandPort? registryCommandPort = null,
        IActorRuntime? actorRuntime = null,
        IChatHistoryCommandPort? historyCommandPort = null,
        IEventStore? eventStore = null)
    {
        eventStore ??= new InMemoryEventStoreForTests();
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        if (registryCommandPort is not null)
            services.AddSingleton(registryCommandPort);

        if (actorRuntime is not null)
            services.AddSingleton(actorRuntime);

        if (historyCommandPort is not null)
            services.AddSingleton(historyCommandPort);

        return services.BuildServiceProvider();
    }

    private static NyxIdChatGAgent CreateAgent(
        IServiceProvider provider,
        string actorId,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdRelayOptions? relayOptions = null,
        TimeProvider? timeProvider = null)
    {
        var agent = new NyxIdChatGAgent(
            llmProviderFactory,
            toolSources: toolSources,
            relayOptions: relayOptions,
            timeProvider: timeProvider)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = Guid.NewGuid().ToString("N") },
    };

    private sealed class RecordingGAgentActorRegistryCommandPort : IGAgentActorRegistryCommandPort
    {
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            RegisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(registration, RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            UnregisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingChatHistoryCommandPort : IChatHistoryCommandPort
    {
        public List<SavedChatHistory> Saved { get; } = [];
        public List<(string ScopeId, string ConversationId)> Deleted { get; } = [];

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            Saved.Add(new SavedChatHistory(scopeId, conversationId, meta, messages.ToArray()));
            return Task.CompletedTask;
        }

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
        {
            Deleted.Add((scopeId, conversationId));
            return Task.FromResult(ChatHistoryDeleteResult.Accepted());
        }
    }

    private sealed record SavedChatHistory(
        string ScopeId,
        string ConversationId,
        ConversationMeta Meta,
        IReadOnlyList<StoredChatMessage> Messages);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        public void Advance(TimeSpan amount) => _value = _value.Add(amount);
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> DestroyedActors { get; } = [];

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new RecordingActor(id));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActors.Add(id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(true);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording-agent";
        public Task<string> GetDescriptionAsync() => Task.FromResult("recording-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StreamingToolLoopProviderFactory(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> responses)
        : ILLMProviderFactory, ILLMProvider
    {
        private int _streamIndex;

        public string Name => NyxIdChatServiceDefaults.ProviderName;

        public List<LLMRequest> StreamRequests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamRequests.Add(request);

            var responseIndex = _streamIndex++;
            foreach (var chunk in responses[responseIndex])
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ThrowingStreamingProviderFactory(Exception exception)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => NyxIdChatServiceDefaults.ProviderName;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk();
            await Task.Yield();
            throw exception;
        }
    }

    private static async Task<JsonObject> ReadUntilFrameAsync(
        ChannelReader<JsonObject> reader,
        ICollection<JsonObject> observed,
        string expectedType,
        CancellationToken ct)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ct);
            observed.Add(frame);
            if (string.Equals(FrameType(frame), expectedType, StringComparison.Ordinal))
                return frame;
        }
    }

    private static string FrameType(JsonObject frame) =>
        frame["type"]?.GetValue<string>() ?? string.Empty;

    private sealed class ControlledProgressProviderFactory(bool emitTextToolCall)
        : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _firstRoundRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _round;

        public TaskCompletionSource WaitingForFirstRoundRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstRoundReleased { get; private set; }
        public string Name => NyxIdChatServiceDefaults.ProviderName;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (_round++ == 0)
            {
                yield return new LLMStreamChunk { DeltaContent = "first chunk" };
                WaitingForFirstRoundRelease.TrySetResult();
                await _firstRoundRelease.Task.WaitAsync(ct);
                yield return emitTextToolCall
                    ? new LLMStreamChunk
                    {
                        DeltaContent = """
                            <function_calls>
                            <invoke name="controlled_lookup">
                            <parameter name="input">controlled</parameter>
                            </invoke>
                            </function_calls>
                            """,
                    }
                    : new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "controlled-call-1",
                        Name = "controlled_lookup",
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = "final answer",
                    Usage = new TokenUsage(3, 2, 5),
                };
            }

            yield return new LLMStreamChunk { IsLast = true };
        }

        public void ReleaseFirstRound()
        {
            FirstRoundReleased = true;
            _firstRoundRelease.TrySetResult();
        }
    }

    private sealed class ControlledProgressTool : IAgentTool
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released { get; private set; }
        private string _displayName = "Controlled lookup";
        public string Name => "controlled_lookup";
        public string Description => "Looks up controlled test data.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;
        public Aevatar.Foundation.Abstractions.Tools.ToolPresentationDescriptor Presentation =>
            ToolPresentationDescriptors.BuiltIn(Name, _displayName, Description);

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _displayName = "Renamed after invocation start";
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }

        public void Release(string result)
        {
            Released = true;
            _release.TrySetResult(result);
        }
    }

    private sealed class FlushedSseFrameStream : MemoryStream
    {
        private readonly Channel<JsonObject> _frames =
            Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly Queue<JsonObject> _pending = [];

        public ChannelReader<JsonObject> Frames => _frames.Reader;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            var raw = Encoding.UTF8.GetString(buffer.Span).Trim();
            if (raw.StartsWith("data: ", StringComparison.Ordinal) &&
                JsonNode.Parse(raw[6..]) is JsonObject frame)
            {
                _pending.Enqueue(frame);
            }

            return write;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            while (_pending.TryDequeue(out var frame))
                await _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _frames.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    private sealed class FixedNyxIdResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => $"{name} test tool";
        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
    }

    private sealed class VerifiedMissingServiceTool : IAgentTool
    {
        public string Name => "nyxid_require_service";
        public string Description => "Verified missing service test fixture";
        public string ParametersSchema => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(
                """{"blocked":true,"service_slug":"api-github","reason_code":"USER_SERVICE_NOT_VISIBLE","safe_message":"No caller-visible NyxID UserService matches the requested service."}""");

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.AuthorizationRequired,
                ErrorCode = "USER_SERVICE_NOT_VISIBLE",
                ErrorMessage = "No caller-visible NyxID UserService matches the requested service.",
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "api-github",
                    ResourceUri = "/repos/private",
                    ReasonCode = "USER_SERVICE_NOT_VISIBLE",
                    SafeMessage = "No caller-visible NyxID UserService matches the requested service.",
                },
            };
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }
}
