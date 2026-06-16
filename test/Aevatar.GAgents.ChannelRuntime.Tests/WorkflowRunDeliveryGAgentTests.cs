using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowRunDeliveryGAgentTests
{
    private const string DeliveryActorId = "workflow-run-delivery:workflow-actor:wf-command";

    [Fact]
    public async Task TerminalWorkflowEvent_ShouldDispatchContinuationAndReplyWithDurableCredential()
    {
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var nyxHandler = new RecordingJsonHandler();
        var outboundPort = CreateOutboundPort(nyxHandler);
        var deferredDispatchPort = new DeferredDispatchPort();
        var credentialProvider = new RecordingCredentialProvider
        {
            ["secrets://nyx/reply-1"] = "nyxid_ag_secret_1",
        };
        var agent = await CreateAgentAsync(projectionPort, outboundPort, deferredDispatchPort, credentialProvider);
        var dispatchPort = new DirectActorDispatchPort(agent);
        deferredDispatchPort.Inner = dispatchPort;

        await agent.HandleEventAsync(Envelope(StartRequest()));

        projectionPort.LastSink.Should().NotBeNull();
        await projectionPort.LastSink!.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "workflow completed text" }),
            },
        });

        dispatchPort.Dispatches.Should().ContainSingle();
        var continuation = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<WorkflowRunDeliveryTerminalFrameObserved>();
        continuation.Status.Should().Be("completed");
        continuation.Text.Should().Be("workflow completed text");
        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        agent.State.TerminalStatus.Should().Be("completed");
        agent.State.TerminalText.Should().Be("workflow completed text");
        nyxHandler.Requests.Should().ContainSingle();
        nyxHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer nyxid_ag_secret_1");
        nyxHandler.Requests[0].Body.Should().Contain("\"message_id\":\"reply-message-1\"");
        nyxHandler.Requests[0].Body.Should().Contain("\"text\":\"workflow completed text\"");
        credentialProvider.ResolvedRefs.Should().ContainSingle("secrets://nyx/reply-1");
    }

    [Fact]
    public async Task StartValidationFailure_ShouldPersistFailedTerminalState()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(
            new RecordingWorkflowExecutionProjectionPort(),
            CreateOutboundPort(new RecordingJsonHandler()),
            new DeferredDispatchPort(),
            new RecordingCredentialProvider(),
            eventStore);

        var invalid = StartRequest();
        invalid.ReplyMessageId = string.Empty;
        await agent.HandleEventAsync(Envelope(invalid));

        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        agent.State.ErrorCode.Should().Be("reply_message_id_required");
        var failed = await LastFailedEventAsync(eventStore);
        failed.ErrorCode.Should().Be("reply_message_id_required");
        failed.Attempt.Should().Be(0);
    }

    [Fact]
    public async Task ProjectionDisabled_ShouldPersistFailedTerminalState()
    {
        var eventStore = new InMemoryEventStore();
        var projectionPort = new RecordingWorkflowExecutionProjectionPort { ProjectionEnabledValue = false };
        var agent = await CreateAgentAsync(
            projectionPort,
            CreateOutboundPort(new RecordingJsonHandler()),
            new DeferredDispatchPort(),
            new RecordingCredentialProvider(),
            eventStore);

        await agent.HandleEventAsync(Envelope(StartRequest()));

        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        agent.State.ErrorCode.Should().Be("projection_disabled");
        projectionPort.LastSink.Should().BeNull();
        var failed = await LastFailedEventAsync(eventStore);
        failed.ErrorCode.Should().Be("projection_disabled");
    }

    [Fact]
    public async Task ProjectionUnavailable_ShouldPersistFailedTerminalState()
    {
        var eventStore = new InMemoryEventStore();
        var projectionPort = new RecordingWorkflowExecutionProjectionPort { ReturnAttachment = false };
        var agent = await CreateAgentAsync(
            projectionPort,
            CreateOutboundPort(new RecordingJsonHandler()),
            new DeferredDispatchPort(),
            new RecordingCredentialProvider(),
            eventStore);

        await agent.HandleEventAsync(Envelope(StartRequest()));

        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        agent.State.ErrorCode.Should().Be("projection_unavailable");
        var failed = await LastFailedEventAsync(eventStore);
        failed.ErrorCode.Should().Be("projection_unavailable");
    }

    [Fact]
    public async Task OutboundSendFailure_ShouldPersistFailedTerminalState()
    {
        var eventStore = new InMemoryEventStore();
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var nyxHandler = new RecordingJsonHandler(HttpStatusCode.BadRequest, """{"error":"invalid_reply"}""");
        var credentialProvider = new RecordingCredentialProvider
        {
            ["secrets://nyx/reply-1"] = "nyxid_ag_secret_1",
        };
        var deferredDispatchPort = new DeferredDispatchPort();
        var agent = await CreateAgentAsync(
            projectionPort,
            CreateOutboundPort(nyxHandler),
            deferredDispatchPort,
            credentialProvider,
            eventStore);
        var dispatchPort = new DirectActorDispatchPort(agent);
        deferredDispatchPort.Inner = dispatchPort;

        await agent.HandleEventAsync(Envelope(StartRequest()));
        await projectionPort.LastSink!.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "workflow completed text" }),
            },
        });

        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        agent.State.ErrorCode.Should().Be("relay_reply_rejected");
        agent.State.DeliveryAttempt.Should().Be(1);
        agent.State.TerminalStatus.Should().Be("completed");
        var failed = await LastFailedEventAsync(eventStore);
        failed.ErrorCode.Should().Be("relay_reply_rejected");
        failed.Attempt.Should().Be(1);
        failed.TerminalText.Should().Be("workflow completed text");
    }

    [Fact]
    public async Task TerminalWorkflowEvent_WhenContinuationDispatchFails_ShouldRemainRetryable()
    {
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var nyxHandler = new RecordingJsonHandler();
        var credentialProvider = new RecordingCredentialProvider
        {
            ["secrets://nyx/reply-1"] = "nyxid_ag_secret_1",
        };
        var deferredDispatchPort = new DeferredDispatchPort();
        var agent = await CreateAgentAsync(
            projectionPort,
            CreateOutboundPort(nyxHandler),
            deferredDispatchPort,
            credentialProvider);
        deferredDispatchPort.Inner = new ThrowingDispatchPort(new InvalidOperationException("dispatch rejected"));
        await agent.HandleEventAsync(Envelope(StartRequest()));
        var terminalFrame = new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "workflow completed text" }),
            },
        };

        var failedPush = async () => await projectionPort.LastSink!.PushAsync(terminalFrame);
        await failedPush.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch rejected");
        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);
        nyxHandler.Requests.Should().BeEmpty();

        var dispatchPort = new DirectActorDispatchPort(agent);
        deferredDispatchPort.Inner = dispatchPort;
        await projectionPort.LastSink!.PushAsync(terminalFrame);

        dispatchPort.Dispatches.Should().ContainSingle();
        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        agent.State.TerminalText.Should().Be("workflow completed text");
        nyxHandler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MissingResolvedCredential_ShouldPersistFailedTerminalStateWithoutHttpRequest()
    {
        var eventStore = new InMemoryEventStore();
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var nyxHandler = new RecordingJsonHandler();
        var deferredDispatchPort = new DeferredDispatchPort();
        var agent = await CreateAgentAsync(
            projectionPort,
            CreateOutboundPort(nyxHandler),
            deferredDispatchPort,
            new RecordingCredentialProvider(),
            eventStore);
        var dispatchPort = new DirectActorDispatchPort(agent);
        deferredDispatchPort.Inner = dispatchPort;

        await agent.HandleEventAsync(Envelope(StartRequest()));
        await projectionPort.LastSink!.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "workflow completed text" }),
            },
        });

        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        agent.State.ErrorCode.Should().Be("durable_reply_credential_missing");
        nyxHandler.Requests.Should().BeEmpty();
        var failed = await LastFailedEventAsync(eventStore);
        failed.ErrorCode.Should().Be("durable_reply_credential_missing");
    }

    private static async Task<WorkflowRunDeliveryGAgent> CreateAgentAsync(
        RecordingWorkflowExecutionProjectionPort projectionPort,
        NyxIdRelayOutboundPort outboundPort,
        DeferredDispatchPort dispatchPort,
        RecordingCredentialProvider? credentialProvider = null,
        InMemoryEventStore? eventStore = null)
    {
        eventStore ??= new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowRunDeliveryGAgent(
            projectionPort,
            dispatchPort,
            outboundPort,
            credentialProvider ?? new RecordingCredentialProvider(),
            NullLogger<WorkflowRunDeliveryGAgent>.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDeliveryGAgentState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [DeliveryActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private static EventEnvelope Envelope<T>(T payload)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", DeliveryActorId),
        };

    private static WorkflowRunDeliveryStartRequested StartRequest() =>
        new()
        {
            DeliveryId = DeliveryActorId,
            WorkflowActorId = "workflow-actor",
            WorkflowRunId = "wf-command",
            WorkflowCommandId = "wf-command",
            WorkflowCorrelationId = "wf-correlation",
            StreamTopic = "aevatar://actors/workflow-actor/runs/wf-command",
            ChannelPlatform = "lark",
            ReplyMessageId = "reply-message-1",
            PlatformMessageId = "platform-message-1",
            RegistrationScopeId = "registration-scope-1",
            DurableReplyCredentialRef = "secrets://nyx/reply-1",
        };

    private static async Task<WorkflowRunDeliveryFailedEvent> LastFailedEventAsync(IEventStore eventStore)
    {
        var events = await eventStore.GetEventsAsync(DeliveryActorId);
        return events
            .Where(x => x.EventData.Is(WorkflowRunDeliveryFailedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<WorkflowRunDeliveryFailedEvent>())
            .Last();
    }

    private static NyxIdRelayOutboundPort CreateOutboundPort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") },
            NullLogger<NyxIdApiClient>.Instance);
        return new NyxIdRelayOutboundPort(
            client,
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new PlainTextComposer("lark")]);
    }

    private sealed class DeferredDispatchPort : IActorDispatchPort
    {
        public IActorDispatchPort? Inner { get; set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            (Inner ?? throw new InvalidOperationException("Dispatch port is not bound."))
            .DispatchAsync(actorId, envelope, ct);
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
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

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DirectActorDispatchPort(WorkflowRunDeliveryGAgent agent) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            await agent.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class ThrowingDispatchPort(Exception exception) : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromException<DispatchAdmission>(exception);
    }

    private sealed class RecordingWorkflowExecutionProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabledValue { get; init; } = true;
        public bool ReturnAttachment { get; init; } = true;
        public bool ProjectionEnabled => ProjectionEnabledValue;
        public IEventSink<WorkflowRunEventEnvelope>? LastSink { get; private set; }

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            LastSink = sink;
            if (!ReturnAttachment)
                return Task.FromResult<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?>(null);

            return Task.FromResult<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?>(
                new(new RecordingWorkflowExecutionProjectionLease(rootActorId, commandId), new NoopAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            LastSink = sink;
            return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
        }

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(IWorkflowExecutionProjectionLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record RecordingWorkflowExecutionProjectionLease(string ActorId, string CommandId)
        : IWorkflowExecutionProjectionLease;

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingJsonHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        public RecordingJsonHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string responseBody = """{"message_id":"reply-1","platform_message_id":"platform-1"}""")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class RecordingCredentialProvider : Dictionary<string, string>, ICredentialProvider
    {
        public List<string> ResolvedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            ResolvedRefs.Add(credentialRef);
            return Task.FromResult(TryGetValue(credentialRef, out var value) ? value : null);
        }
    }

    private sealed class PlainTextComposer(string platform) : IMessageComposer<PlainTextPayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public PlainTextPayload Compose(MessageContent intent, ComposeContext context) =>
            new(intent.Text ?? string.Empty);

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) =>
            Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
            ComposeCapability.Exact;
    }

    private sealed record PlainTextPayload(string PlainText) : IPlainTextComposedMessage;
}
