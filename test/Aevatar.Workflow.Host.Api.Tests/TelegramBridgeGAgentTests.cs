using System.Diagnostics;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Extensions.Bridge;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

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
        };
        request.Headers["chat_id"] = "10001";
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
        };
        request.Headers["chat_id"] = "10001";
        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.Content.Should().StartWith("[[AEVATAR_LLM_ERROR]]");
        textEnd.Content.Should().Contain("connector");
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
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["timeout_ms"] = "15000";
        request.Headers["aevatar.llm_timeout_ms"] = "15000";

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
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["telegram.verification_code"] = "123 456";
        request.Headers["telegram.2fa_password"] = "secret-2fa";
        request.Headers["telegram.phone_number"] = "+8613800000000";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Parameters["verification_code"].Should().Be("123 456");
        connector.Received[0].Parameters["password"].Should().Be("secret-2fa");
        connector.Received[0].Parameters["phone_number"].Should().Be("+8613800000000");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWaitReplyOperation_ShouldPollTelegramGroupStream()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":100,"message":{"chat":{"id":"10001"},"from":{"id":"1000","username":"aevatar_bot"},"text":"old-message"}}]}""",
            },
            new ConnectorResponse
            {
                Success = true,
                Output =
                    """{"ok":true,"result":[{"update_id":101,"message":{"chat":{"id":"10001"},"from":{"id":"2002","username":"openclaw_bot"},"text":"openclaw-reply"}}]}""",
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
            Prompt = "wait",
            SessionId = "session-wait",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/waitReply";
        request.Headers["expected_from_username"] = "openclaw_bot";
        request.Headers["wait_timeout_ms"] = "5000";
        request.Headers["poll_timeout_sec"] = "1";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Count.Should().BeGreaterThanOrEqualTo(2);
        connector.Received.Should().OnlyContain(x => x.Operation == "/getUpdates");

        var secondPayload = JsonDocument.Parse(connector.Received[1].Payload).RootElement;
        secondPayload.GetProperty("offset").GetInt64().Should().Be(101);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait");
        textEnd.Content.Should().Be("openclaw-reply");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWaitReplyGetsEditedMessage_ShouldReturnLatestMatchedContent()
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
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "wait-edited",
            SessionId = "session-wait-edited",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/waitReply";
        request.Headers["expected_from_username"] = "openclaw_bot";
        request.Headers["wait_timeout_ms"] = "5000";
        request.Headers["poll_timeout_sec"] = "1";
        request.Headers["start_from_latest"] = "true";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Count.Should().BeGreaterThanOrEqualTo(4);
        connector.Received.Should().OnlyContain(x => x.Operation == "/getUpdates");

        var secondPayload = JsonDocument.Parse(connector.Received[1].Payload).RootElement;
        secondPayload.GetProperty("offset").GetInt64().Should().Be(401);
        var thirdPayload = JsonDocument.Parse(connector.Received[2].Payload).RootElement;
        thirdPayload.GetProperty("offset").GetInt64().Should().Be(402);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-edited");
        textEnd.Content.Should().Be("openclaw-reply-final");
    }

    [Fact]
    public async Task HandleChatRequest_WhenCollectAllRepliesEnabled_ShouldReturnMergedReplies()
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
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "wait-collect-all",
            SessionId = "session-wait-collect-all",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/waitReply";
        request.Headers["expected_from_username"] = "openclaw_bot";
        request.Headers["wait_timeout_ms"] = "5000";
        request.Headers["poll_timeout_sec"] = "1";
        request.Headers["start_from_latest"] = "true";
        request.Headers["collect_all_replies"] = "true";
        request.Headers["settle_polls_after_match"] = "2";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-collect-all");
        textEnd.Content.Should().Be("openclaw-reply-part-1\n\n---\n\nopenclaw-reply-part-2-final");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWaitReplyMatchAppearsInBootstrapBatch_ShouldReturnImmediately()
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
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "wait-bootstrap",
            SessionId = "session-wait-bootstrap",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/waitReply";
        request.Headers["expected_from_username"] = "openclaw_bot";
        request.Headers["correlation_contains"] = "[AEVATAR_STREAM_REPLY]";
        request.Headers["wait_timeout_ms"] = "5000";
        request.Headers["poll_timeout_sec"] = "1";
        request.Headers["start_from_latest"] = "true";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].Operation.Should().Be("/getUpdates");

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-bootstrap");
        textEnd.Content.Should().Be("[AEVATAR_STREAM_REPLY] bootstrap-reply");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWaitReplyUsernameMissing_ShouldFallbackToCorrelationMatch()
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
        var agent = new TelegramBridgeGAgent(
            new NoopActorRuntime(),
            registry)
        {
            EventPublisher = publisher,
            Services = CreateAgentServices(),
        };

        var request = new ChatRequestEvent
        {
            Prompt = "wait-username-missing",
            SessionId = "session-wait-username-missing",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["operation"] = "/waitReply";
        request.Headers["expected_from_username"] = "openclaw_bot";
        request.Headers["correlation_contains"] = "[AEVATAR_STREAM_REPLY]";
        request.Headers["wait_timeout_ms"] = "5000";
        request.Headers["poll_timeout_sec"] = "1";
        request.Headers["start_from_latest"] = "true";

        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.SessionId.Should().Be("session-wait-username-missing");
        textEnd.Content.Should().Be("[AEVATAR_STREAM_REPLY] no-username-reply");
    }

    [Fact]
    public async Task HandleChatRequest_WhenConnectorHangs_ShouldFailByWatchdogBeforeLlmTimeout()
    {
        var connector = new HangingConnector();
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
            SessionId = "session-watchdog-timeout",
        };
        request.Headers["chat_id"] = "10001";
        request.Headers["telegram.timeout_ms"] = "100";
        request.Headers["aevatar.llm_timeout_ms"] = "30000";

        var stopwatch = Stopwatch.StartNew();
        await agent.HandleEventAsync(Envelope(request), CancellationToken.None);
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2_000);

        var textEnd = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        textEnd.Content.Should().StartWith("[[AEVATAR_LLM_ERROR]]");
        textEnd.Content.Should().Contain("watchdog timeout");
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
        };
        request.Headers["chat_id"] = "10001";

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
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return new TaskCompletionSource<ConnectorResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
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
            where TEvent : IMessage =>
            Task.CompletedTask;
    }

    private static IServiceProvider CreateAgentServices()
    {
        return new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .BuildServiceProvider();
    }

    private sealed class NoopRuntimeCallbackScheduler : Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler
    {
        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleTimeoutAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleTimerAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new NoopActor(id ?? Guid.NewGuid().ToString("N")));

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new NoopActor(id ?? Guid.NewGuid().ToString("N")));

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
