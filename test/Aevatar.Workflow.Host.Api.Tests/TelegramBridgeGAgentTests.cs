using System.Diagnostics;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Extensions.Bridge;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class TelegramBridgeGAgentTests
{
    [Fact]
    public async Task HandleChatRequest_WhenConnectorSucceeds_ShouldPublishTextMessageEnd()
    {
        var connector = new RecordingConnector(new ConnectorResponse
        {
            Success = true,
            Output = """{"ok":true,"result":{"text":"telegram-ok"}}""",
        });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "hello telegram",
            SessionId = "session-1",
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
                Operation = TelegramBridgeOperation.SendMessage,
            },
        };
        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        var connectorRequest = connector.Received[0];
        connectorRequest.Operation.Should().Be("/sendMessage");
        connectorRequest.Parameters["method"].Should().Be("POST");
        var payload = JsonDocument.Parse(connectorRequest.Payload).RootElement;
        payload.GetProperty("chat_id").GetString().Should().Be("10001");
        payload.GetProperty("text").GetString().Should().Be("hello telegram");

        publisher.Published.Should().ContainSingle();
        var textEnd = publisher.Published[0].evt.Should().BeOfType<TextMessageEndEvent>().Subject;
        textEnd.SessionId.Should().Be("session-1");
        textEnd.Content.Should().Be("telegram-ok");
        publisher.Published[0].direction.Should().Be(TopologyAudience.Parent);
    }

    [Fact]
    public async Task HandleChatRequest_WhenConnectorMissing_ShouldPublishFailureMarker()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            new InMemoryConnectorRegistry())
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "hello telegram",
            SessionId = "session-2",
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
            },
        };
        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.Content.Should().StartWith("[[AEVATAR_LLM_ERROR]]");
        textEnd.Content.Should().Contain("connector");
    }

    [Fact]
    public async Task HandleChatRequest_WhenOnlyLegacyHeadersProvided_ShouldNotDriveTelegramControl()
    {
        var connector = new RecordingConnector(new ConnectorResponse
        {
            Success = true,
            Output = """{"ok":true,"result":{"text":"telegram-ok"}}""",
        });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "legacy header should not route",
            SessionId = "session-legacy-header",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/sendMessage";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().BeEmpty();
        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.Content.Should().StartWith("[[AEVATAR_LLM_ERROR]]");
        textEnd.Content.Should().Contain("chat_id");
    }

    [Fact]
    public async Task HandleChatRequest_WhenNoExplicitTelegramTimeout_ShouldKeepConnectorTimeoutBelowLlmTimeout()
    {
        var connector = new RecordingConnector(new ConnectorResponse
        {
            Success = true,
            Output = """{"ok":true,"result":{"text":"ok"}}""",
        });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-timeout-buffer",
            TimeoutMs = 15000,
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
            },
        };

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Parameters["timeout_ms"].Should().Be("14000");
    }

    [Fact]
    public async Task HandleChatRequest_WhenTelegramUserRuntimeLoginMetadataProvided_ShouldForwardToConnectorParameters()
    {
        var connector = new RecordingConnector(
            "telegram_user",
            new ConnectorResponse
            {
                Success = true,
                Output = """{"ok":true,"result":{"text":"ok"}}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramUserBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-runtime-login",
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
                VerificationCode = "123 456",
                Password = "secret-2fa",
                PhoneNumber = "+8613800000000",
            },
        };

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Parameters["verification_code"].Should().Be("123 456");
        connector.Received[0].Parameters["password"].Should().Be("secret-2fa");
        connector.Received[0].Parameters["phone_number"].Should().Be("+8613800000000");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWaitReplyOperation_ShouldDispatchTaskScopedWaitActor()
    {
        var connector = new RecordingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var runtime = new NoopActorRuntime();
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            runtime,
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "wait",
            SessionId = "session-wait",
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
                Operation = TelegramBridgeOperation.WaitReply,
                ExpectedFromUsername = "openclaw_bot",
                WaitTimeoutMs = 5000,
                PollTimeoutSeconds = 1,
            },
        };

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().BeEmpty();
        runtime.CreatedActorTypes.Should().ContainSingle().Which.Should().Be(typeof(TelegramWaitReplyGAgent));
        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].targetActorId.Should().StartWith("telegram-wait-reply-session-wait");

        var command = publisher.Sent[0].evt.Should().BeOfType<TelegramWaitForReplyCommand>().Subject;
        command.SessionId.Should().Be("session-wait");
        command.ConnectorName.Should().Be("telegram");
        command.ExpectedChatId.Should().Be("10001");
        command.ExpectedFromUsername.Should().Be("openclaw_bot");
        command.WaitTimeoutMs.Should().Be(5000);
        command.PollTimeoutSeconds.Should().Be(1);
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenWaitReplyGetsEditedMessage_ShouldReturnLatestMatchedContent()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":400,"message":{"chat":{"id":"10001"},"from":{"id":"1000","username":"aevatar_bot"},"text":"old-message"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":401,"message":{"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply-partial"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":402,"message":{"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply-final"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output = """{"ok":true,"result":[]}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var command = BuildWaitReplyCommand(
            sessionId: "session-wait-edited",
            expectedUsername: "openclaw_bot");
        command.StartFromLatest = true;

        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);

        connector.Received.Should().BeEmpty();
        publisher.Sent.Select(x => x.evt).Should().ContainSingle(x => x is TelegramWaitReplyBootstrapDueEvent);

        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch);

        connector.Received.Count.Should().Be(4);
        connector.Received.Should().OnlyContain(x => x.Operation == "/getUpdates");

        var secondPayload = JsonDocument.Parse(connector.Received[1].Payload).RootElement;
        secondPayload.GetProperty("offset").GetInt64().Should().Be(401);
        var thirdPayload = JsonDocument.Parse(connector.Received[2].Payload).RootElement;
        thirdPayload.GetProperty("offset").GetInt64().Should().Be(402);

        var completed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Single();
        completed.SessionId.Should().Be("session-wait-edited");
        completed.Content.Should().Be("openclaw-reply-final");
    }

    [Fact]
    public async Task TelegramBridgeGAgent_WhenWaitReplyCompleted_ShouldPublishTextMessageEnd()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            new InMemoryConnectorRegistry())
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        await agent.HandleEventAsync(
            Envelope(new TelegramWaitReplyCompletedEvent
            {
                SessionId = "session-wait-edited",
                Content = "openclaw-reply-final",
            }),
            CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-edited");
        textEnd.Content.Should().Be("openclaw-reply-final");
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenCollectAllRepliesEnabled_ShouldReturnMergedReplies()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":500,"message":{"chat":{"id":"10001"},"from":{"id":"1000","username":"aevatar_bot"},"text":"old-message"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":501,"message":{"message_id":9001,"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply-part-1"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":502,"message":{"message_id":9002,"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply-part-2-draft"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":503,"message":{"message_id":9002,"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply-part-2-final"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output = """{"ok":true,"result":[]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output = """{"ok":true,"result":[]}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var command = BuildWaitReplyCommand(
            sessionId: "session-wait-collect-all",
            expectedUsername: "openclaw_bot");
        command.StartFromLatest = true;
        command.CollectAllReplies = true;
        command.SettlePollsAfterMatch = 2;

        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch);

        var completed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Single();
        completed.SessionId.Should().Be("session-wait-collect-all");
        completed.Content.Should().Be("openclaw-reply-part-1\n\n---\n\nopenclaw-reply-part-2-final");
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenWaitReplyMatchAppearsInBootstrapBatch_ShouldReturnImmediately()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":201,"message":{"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"[AEVATAR_STREAM_REPLY] bootstrap-reply"}}]}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var command = BuildWaitReplyCommand(
            sessionId: "session-wait-bootstrap",
            expectedUsername: "openclaw_bot",
            correlationContains: "[AEVATAR_STREAM_REPLY]");
        command.StartFromLatest = true;

        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        connector.Received.Should().BeEmpty();

        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Operation.Should().Be("/getUpdates");

        var completed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Single();
        completed.SessionId.Should().Be("session-wait-bootstrap");
        completed.Content.Should().Be("[AEVATAR_STREAM_REPLY] bootstrap-reply");
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenWaitReplyUsernameMissing_ShouldFallbackToCorrelationMatch()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":301,"message":{"chat":{"id":"10001"},"from":{"id":"2002"},"text":"[AEVATAR_STREAM_REPLY] no-username-reply"}}]}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var command = BuildWaitReplyCommand(
            sessionId: "session-wait-username-missing",
            expectedUsername: "openclaw_bot",
            correlationContains: "[AEVATAR_STREAM_REPLY]");
        command.StartFromLatest = true;

        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch);

        var completed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Single();
        completed.SessionId.Should().Be("session-wait-username-missing");
        completed.Content.Should().Be("[AEVATAR_STREAM_REPLY] no-username-reply");
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenConnectorReturnsOkFalse_ShouldPublishFailedEvent()
    {
        var connector = new RecordingConnector(new ConnectorResponse
        {
            Success = true,
            Output = """{"ok":false,"description":"telegram denied getUpdates"}""",
        });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var command = BuildWaitReplyCommand(
            sessionId: "session-wait-failed",
            expectedUsername: "openclaw_bot");

        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-wait-failed");
        failed.CommandId.Should().Be("cmd-session-wait-failed");
        failed.Error.Should().Be("telegram getUpdates parse failed: telegram denied getUpdates");
        failed.WaitActorId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TelegramBridgeGAgent_WhenWaitReplyFailed_ShouldPublishFailureMarker()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            new InMemoryConnectorRegistry())
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        await agent.HandleEventAsync(
            Envelope(new TelegramWaitReplyFailedEvent
            {
                SessionId = "session-wait-failed",
                Error = "telegram denied getUpdates",
            }),
            CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-failed");
        textEnd.Content.Should().StartWith("[[AEVATAR_LLM_ERROR]]");
        textEnd.Content.Should().Contain("telegram denied getUpdates");
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenGetUpdatesConnectorHangs_ShouldNotBlockActorTurn()
    {
        var connector = new HangingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry);
        await agent.ActivateAsync();

        var stopwatch = Stopwatch.StartNew();
        await agent.HandleEventAsync(Envelope(BuildWaitReplyCommand(
            sessionId: "session-hanging-getupdates",
            expectedUsername: "openclaw_bot")), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500);
        connector.Received.Should().ContainSingle(x => x.Operation == "/getUpdates");
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenGetUpdatesConnectorHangs_ShouldFailFromRequestTimeoutContinuation()
    {
        var connector = new HangingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var (agent, dispatch) = await CreateActivatedWaitReplyAgentAsync(registry, publisher, scheduler);

        var command = BuildWaitReplyCommand(
            sessionId: "session-hanging-getupdates-timeout",
            expectedUsername: "openclaw_bot");
        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);

        var timeout = scheduler.Timeouts.Should().ContainSingle().Subject;
        var timeoutDue = timeout.TriggerEnvelope.Payload.Unpack<TelegramWaitReplyTimeoutDueEvent>();
        timeoutDue.CommandId.Should().Be(command.CommandId);
        timeoutDue.Generation.Should().Be(agent.State.Generation);
        timeoutDue.RequestId.Should().Be(agent.State.PendingGetUpdates.RequestId);
        timeoutDue.TimeoutMs.Should().Be(4000);

        await agent.HandleEventAsync(timeout.TriggerEnvelope, CancellationToken.None);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-hanging-getupdates-timeout");
        failed.Error.Should().Be("telegram getUpdates timeout after 4000ms");
        agent.State.Active.Should().BeFalse();
        agent.State.PendingGetUpdates.Should().BeNull();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenGetUpdatesTimeoutRequestIdIsStale_ShouldIgnoreTimeout()
    {
        var connector = new HangingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var (agent, dispatch) = await CreateActivatedWaitReplyAgentAsync(registry, publisher);

        var command = BuildWaitReplyCommand(
            sessionId: "session-stale-timeout",
            expectedUsername: "openclaw_bot");
        await agent.HandleEventAsync(Envelope(command), CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);

        agent.State.PendingGetUpdates.Should().NotBeNull();
        var pendingRequest = agent.State.PendingGetUpdates.Clone();

        await agent.HandleEventAsync(
            Envelope(new TelegramWaitReplyTimeoutDueEvent
            {
                CommandId = command.CommandId,
                Generation = agent.State.Generation,
                RequestId = "stale-request",
                TimeoutMs = 4000,
            }),
            CancellationToken.None);

        agent.State.Active.Should().BeTrue();
        agent.State.PendingGetUpdates.Should().BeEquivalentTo(pendingRequest);
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenExternalLinkInactive_ShouldPublishFailedEvent()
    {
        var connector = new RecordingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        await agent.HandleEventAsync(
            Envelope(BuildWaitReplyCommand("session-inactive-link", "openclaw_bot")),
            CancellationToken.None);
        await DrainWaitReplySelfEventsWithoutExternalLinksAsync(agent, publisher, maxTurns: 1);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-inactive-link");
        failed.Error.Should().Be("telegram getUpdates external link is not active");
        agent.State.Active.Should().BeFalse();
        agent.State.PendingGetUpdates.Should().BeNull();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenExternalLinkDispatchFails_ShouldPublishFailedEvent()
    {
        var connector = new RecordingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(
            dispatch,
            registry,
            new RecordingRuntimeCallbackScheduler(),
            new ThrowingSendTransportFactory("dispatch offline"));
        await agent.ActivateAsync();

        await agent.HandleEventAsync(
            Envelope(BuildWaitReplyCommand("session-dispatch-fails", "openclaw_bot")),
            CancellationToken.None);
        await agent.HandleEventAsync(
            Envelope(publisher.PopSentSelfEvent<TelegramWaitReplyPollDueEvent>(agent.Id)),
            CancellationToken.None);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-dispatch-fails");
        failed.Error.Should().Be("telegram getUpdates dispatch failed: dispatch offline");
        agent.State.Active.Should().BeFalse();
        agent.State.PendingGetUpdates.Should().BeNull();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenExternalLinkPayloadInvalid_ShouldPublishFailedEvent()
    {
        var connector = new HangingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var (agent, dispatch) = await CreateActivatedWaitReplyAgentAsync(registry, publisher);

        await agent.HandleEventAsync(
            Envelope(BuildWaitReplyCommand("session-invalid-payload", "openclaw_bot")),
            CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);

        await agent.HandleEventAsync(Envelope(new ExternalLinkMessageReceivedEvent
        {
            LinkId = "telegram-get-updates",
            RawPayload = ByteString.CopyFrom([0xFF]),
            ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-invalid-payload");
        failed.Error.Should().StartWith("telegram getUpdates result parse failed:");
        agent.State.Active.Should().BeFalse();
        agent.State.PendingGetUpdates.Should().BeNull();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenGetUpdatesResultRequestIdIsStale_ShouldIgnoreResult()
    {
        var connector = new HangingConnector();
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var (agent, dispatch) = await CreateActivatedWaitReplyAgentAsync(registry, publisher);

        await agent.HandleEventAsync(
            Envelope(BuildWaitReplyCommand("session-stale-result", "openclaw_bot")),
            CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);

        await agent.HandleEventAsync(Envelope(new ExternalLinkMessageReceivedEvent
        {
            LinkId = "telegram-get-updates",
            RawPayload = new TelegramGetUpdatesResult
            {
                CommandId = agent.State.CommandId,
                Generation = agent.State.Generation,
                RequestId = "stale-request",
                Success = true,
                Output = """{"ok":true,"result":[]}""",
            }.ToByteString(),
            ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        agent.State.Active.Should().BeTrue();
        agent.State.PendingGetUpdates.Should().NotBeNull();
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task TelegramWaitReplyGAgent_WhenGetUpdatesResultIsUnsuccessful_ShouldPublishFailedEvent()
    {
        var connector = new AsyncFaultingConnector(new InvalidOperationException("remote fault"));
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var (agent, dispatch) = await CreateActivatedWaitReplyAgentAsync(registry, publisher);

        await agent.HandleEventAsync(
            Envelope(BuildWaitReplyCommand("session-unsuccessful-result", "openclaw_bot")),
            CancellationToken.None);
        await DrainWaitReplySelfEventsAsync(agent, publisher, dispatch, maxTurns: 1);

        var failed = publisher.Published.Select(x => x.evt).OfType<TelegramWaitReplyFailedEvent>().Single();
        failed.SessionId.Should().Be("session-unsuccessful-result");
        failed.Error.Should().Be("telegram getUpdates execution failed: remote fault");
        agent.State.Active.Should().BeFalse();
        agent.State.PendingGetUpdates.Should().BeNull();
    }

    [Fact]
    public void TelegramWaitReplyProductionPath_ShouldNotReintroduceInTurnWatchdogRace()
    {
        typeof(TelegramBridgeGAgent)
            .GetMethod(
                "ExecuteConnectorWithWatchdogAsync",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task TelegramUserBridgeGAgent_WhenConnectorNotSpecified_ShouldUseTelegramUserConnectorByDefault()
    {
        var connector = new RecordingConnector(
            "telegram_user",
            new ConnectorResponse
            {
                Success = true,
                Output = """{"ok":true,"result":{"text":"telegram-user-ok"}}""",
            });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var publisher = new RecordingEventPublisher();
        var agent = new TelegramUserBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "hello telegram user",
            SessionId = "session-user-1",
            Telegram = new TelegramBridgeRequest
            {
                ChatId = "10001",
            },
        };

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Connector.Should().Be("telegram_user");
        connector.Received[0].Operation.Should().Be("/sendMessage");
        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.Content.Should().Be("telegram-user-ok");
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };
    }

    private static async Task DrainWaitReplySelfEventsAsync(
        TelegramWaitReplyGAgent agent,
        RecordingEventPublisher publisher,
        RecordingActorDispatchPort dispatch,
        int maxTurns = 16)
    {
        for (var i = 0; i < maxTurns; i++)
        {
            await dispatch.DrainAsync();

            var nextIndex = publisher.Sent.FindIndex(x =>
                x.targetActorId == agent.Id &&
                x.evt is TelegramWaitReplyBootstrapDueEvent or TelegramWaitReplyPollDueEvent or TelegramWaitReplyTimeoutDueEvent);
            if (nextIndex < 0)
                return;

            var next = publisher.Sent[nextIndex];
            publisher.Sent.RemoveAt(nextIndex);
            await agent.HandleEventAsync(Envelope(next.evt), CancellationToken.None);
            await dispatch.DrainAsync();
        }

        await dispatch.DrainAsync();
        if (publisher.Sent.Any(x =>
                x.targetActorId == agent.Id &&
                x.evt is TelegramWaitReplyBootstrapDueEvent or TelegramWaitReplyPollDueEvent or TelegramWaitReplyTimeoutDueEvent))
        {
            throw new InvalidOperationException("wait-reply self event drain exceeded max turns");
        }
    }

    private static async Task DrainWaitReplySelfEventsWithoutExternalLinksAsync(
        TelegramWaitReplyGAgent agent,
        RecordingEventPublisher publisher,
        int maxTurns = 16)
    {
        for (var i = 0; i < maxTurns; i++)
        {
            var nextIndex = publisher.Sent.FindIndex(x =>
                x.targetActorId == agent.Id &&
                x.evt is TelegramWaitReplyBootstrapDueEvent or TelegramWaitReplyPollDueEvent or TelegramWaitReplyTimeoutDueEvent);
            if (nextIndex < 0)
                return;

            var next = publisher.Sent[nextIndex];
            publisher.Sent.RemoveAt(nextIndex);
            await agent.HandleEventAsync(Envelope(next.evt), CancellationToken.None);
        }

        if (publisher.Sent.Any(x =>
                x.targetActorId == agent.Id &&
                x.evt is TelegramWaitReplyBootstrapDueEvent or TelegramWaitReplyPollDueEvent or TelegramWaitReplyTimeoutDueEvent))
        {
            throw new InvalidOperationException("wait-reply self event drain exceeded max turns");
        }
    }

    private static Task<(TelegramWaitReplyGAgent Agent, RecordingActorDispatchPort Dispatch)> CreateActivatedWaitReplyAgentAsync(
        IConnectorRegistry registry,
        RecordingEventPublisher publisher)
    {
        return CreateActivatedWaitReplyAgentAsync(registry, publisher, new RecordingRuntimeCallbackScheduler());
    }

    private static async Task<(TelegramWaitReplyGAgent Agent, RecordingActorDispatchPort Dispatch)> CreateActivatedWaitReplyAgentAsync(
        IConnectorRegistry registry,
        RecordingEventPublisher publisher,
        RecordingRuntimeCallbackScheduler scheduler)
    {
        var agent = new TelegramWaitReplyGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventSourcing = new RecordingEventSourcing<TelegramWaitReplyState>(
                (state, evt) => TelegramWaitReplyStateTransitions.Apply(state, evt)),
            EventPublisher = publisher,
        };
        var dispatch = new RecordingActorDispatchPort(agent);
        agent.Services = CreateAgentServices(dispatch, registry, scheduler);
        await agent.ActivateAsync();
        return (agent, dispatch);
    }

    private static TelegramWaitForReplyCommand BuildWaitReplyCommand(
        string sessionId,
        string expectedUsername,
        string correlationContains = "")
    {
        var command = new TelegramWaitForReplyCommand
        {
            CommandId = $"cmd-{sessionId}",
            SessionId = sessionId,
            ConnectorName = "telegram",
            ExpectedChatId = "10001",
            ExpectedFromUsername = expectedUsername,
            CorrelationContains = correlationContains,
            WaitTimeoutMs = 5000,
            PollTimeoutSeconds = 1,
            SettlePollsAfterMatch = 1,
            StartFromLatest = false,
        };
        command.ConnectorParameters["method"] = "POST";
        command.ConnectorParameters["content_type"] = "application/json";
        return command;
    }

    private sealed class RecordingConnector : IConnector
    {
        private readonly IReadOnlyList<ConnectorResponse> _responses;
        private readonly string _name;
        private int _responseIndex;

        public RecordingConnector(params ConnectorResponse[] responses)
            : this("telegram", responses)
        {
        }

        public RecordingConnector(string name, params ConnectorResponse[] responses)
        {
            _name = name;
            _responses = responses.Length == 0
                ? [new ConnectorResponse { Success = false, Error = "no connector response configured" }]
                : responses;
        }

        public RecordingConnector(string name, IReadOnlyList<ConnectorResponse> responses)
        {
            _name = name;
            _responses = responses.Count == 0
                ? [new ConnectorResponse { Success = false, Error = "no connector response configured" }]
                : responses;
        }

        public List<ConnectorRequest> Received { get; } = [];
        public string Name => _name;
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            Received.Add(request);
            var index = Math.Min(_responseIndex, _responses.Count - 1);
            _responseIndex++;
            return Task.FromResult(_responses[index]);
        }
    }

    private sealed class HangingConnector : IConnector
    {
        public List<ConnectorRequest> Received { get; } = [];
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            Received.Add(request);
            _ = ct;
            return new TaskCompletionSource<ConnectorResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class AsyncFaultingConnector(Exception exception) : IConnector
    {
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromException<ConnectorResponse>(exception);
        }
    }

    private sealed class InMemoryConnectorRegistry : IConnectorRegistry
    {
        private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);

        public void Register(IConnector connector) => _connectors[connector.Name] = connector;

        public bool TryGet(string name, out IConnector? connector) => _connectors.TryGetValue(name, out connector);

        public IReadOnlyList<string> ListNames() => _connectors.Keys.ToList();
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage evt, TopologyAudience direction)> Published { get; } = [];
        public List<(string targetActorId, IMessage evt)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = options;
            Published.Add((evt, direction));
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
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }

        public TEvent PopSentSelfEvent<TEvent>(string targetActorId)
            where TEvent : IMessage
        {
            var index = Sent.FindIndex(x => x.targetActorId == targetActorId && x.evt is TEvent);
            index.Should().BeGreaterThanOrEqualTo(0);
            var evt = Sent[index].evt.Should().BeOfType<TEvent>().Subject;
            Sent.RemoveAt(index);
            return evt;
        }
    }

    private sealed class RecordingEventSourcing<TState>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var committedEvents = _pending.Select((evt, index) => new StateEvent
            {
                AgentId = "test-agent",
                EventId = Guid.NewGuid().ToString("N"),
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = CurrentVersion + index + 1,
            }).ToList();
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = "test-agent",
                LatestVersion = CurrentVersion,
                CommittedEvents = { committedEvents },
            });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
    }

    private static class TelegramWaitReplyStateTransitions
    {
        public static TelegramWaitReplyState Apply(TelegramWaitReplyState current, IMessage evt)
        {
            return evt switch
            {
                TelegramWaitReplyStartedEvent started => started.State.Clone(),
                TelegramWaitReplyProgressedEvent progressed => progressed.State.Clone(),
                TelegramWaitReplyClearedEvent cleared => ApplyCleared(current, cleared),
                _ => current,
            };
        }

        private static TelegramWaitReplyState ApplyCleared(
            TelegramWaitReplyState current,
            TelegramWaitReplyClearedEvent cleared)
        {
            if (!string.Equals(current.CommandId, cleared.CommandId, StringComparison.Ordinal) ||
                current.Generation != cleared.Generation)
            {
                return current;
            }

            var next = current.Clone();
            next.Active = false;
            next.PendingGetUpdates = null;
            next.PendingMatchedUpdate = null;
            next.CollectedReplies.Clear();
            next.CollectedReplyOrder.Clear();
            return next;
        }
    }

    private static IServiceProvider CreateAgentServices(
        RecordingActorDispatchPort? dispatchPort = null,
        IConnectorRegistry? connectorRegistry = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        IExternalLinkTransportFactory? externalLinkTransportFactory = null)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(callbackScheduler ?? new NoopRuntimeCallbackScheduler());
        if (externalLinkTransportFactory != null)
            services.AddSingleton(externalLinkTransportFactory);
        else
            services.AddSingleton<IExternalLinkTransportFactory, TelegramGetUpdatesExternalLinkTransportFactory>();
        if (dispatchPort != null)
            services.AddSingleton<IActorDispatchPort>(dispatchPort);
        if (connectorRegistry != null)
            services.AddSingleton(connectorRegistry);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingActorDispatchPort(TelegramWaitReplyGAgent agent) : IActorDispatchPort
    {
        private readonly Queue<EventEnvelope> _pending = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            actorId.Should().Be(agent.Id);
            _pending.Enqueue(envelope);
            return Task.CompletedTask;
        }

        public async Task DrainAsync()
        {
            while (_pending.Count > 0)
            {
                var envelope = _pending.Dequeue();
                await agent.HandleEventAsync(envelope, CancellationToken.None);
            }
        }
    }

    private sealed class ThrowingSendTransportFactory(string message) : IExternalLinkTransportFactory
    {
        public bool CanCreate(string transportType) =>
            string.Equals(transportType, TelegramGetUpdatesExternalLinkTransport.TransportTypeName, StringComparison.OrdinalIgnoreCase);

        public IExternalLinkTransport Create() => new ThrowingSendTransport(message);
    }

    private sealed class ThrowingSendTransport(string message) : IExternalLinkTransport
    {
        public string TransportType => TelegramGetUpdatesExternalLinkTransport.TransportTypeName;

        public IExternalLinkSignalSink? SignalSink { private get; set; }

        public Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct)
        {
            _ = descriptor;
            return SignalSink?.PublishStateChangedAsync(
                new ExternalLinkTransportStateChangedSignal
                {
                    State = ExternalLinkTransportStateSignalKind.Connected,
                    Reason = string.Empty,
                },
                ct) ?? Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            _ = payload;
            _ = ct;
            throw new InvalidOperationException(message);
        }

        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopActorRuntime : IActorRuntime
    {
        public List<System.Type> CreatedActorTypes { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = ct;
            CreatedActorTypes.Add(agentType);
            return Task.FromResult<IActor>(new NoopActor(id ?? Guid.NewGuid().ToString("N")));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent(id);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

}
