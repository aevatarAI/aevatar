using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
    [Fact]
    public async Task TerminalWorkflowEvent_ShouldDispatchContinuationAndReplyWithBotAgentKey()
    {
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var nyxHandler = new RecordingJsonHandler();
        var outboundPort = CreateOutboundPort(nyxHandler);
        var deferredDispatchPort = new DeferredDispatchPort();
        var agent = await CreateAgentAsync(projectionPort, outboundPort, deferredDispatchPort);
        var dispatchPort = new DirectActorDispatchPort(agent);
        deferredDispatchPort.Inner = dispatchPort;

        await agent.HandleEventAsync(Envelope(new WorkflowRunDeliveryStartRequested
        {
            DeliveryId = "workflow-run-delivery:workflow-actor:wf-command",
            WorkflowActorId = "workflow-actor",
            WorkflowRunId = "wf-command",
            WorkflowCommandId = "wf-command",
            WorkflowCorrelationId = "wf-correlation",
            StreamTopic = "aevatar://actors/workflow-actor/runs/wf-command",
            ChannelPlatform = "lark",
            ReplyMessageId = "reply-message-1",
            PlatformMessageId = "platform-message-1",
            BotAgentKeyId = "bot-agent-key-1",
            RegistrationScopeId = "registration-scope-1",
        }));

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
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer bot-agent-key-1");
        nyxHandler.Requests[0].Body.Should().Contain("\"message_id\":\"reply-message-1\"");
        nyxHandler.Requests[0].Body.Should().Contain("\"text\":\"workflow completed text\"");
    }

    private static async Task<WorkflowRunDeliveryGAgent> CreateAgentAsync(
        RecordingWorkflowExecutionProjectionPort projectionPort,
        NyxIdRelayOutboundPort outboundPort,
        DeferredDispatchPort dispatchPort)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowRunDeliveryGAgent(
            projectionPort,
            dispatchPort,
            outboundPort,
            NullLogger<WorkflowRunDeliveryGAgent>.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDeliveryGAgentState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, ["workflow-run-delivery:workflow-actor:wf-command"]);
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
            Route = EnvelopeRouteSemantics.CreateDirect("test", "workflow-run-delivery:workflow-actor:wf-command"),
        };

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

    private sealed class RecordingWorkflowExecutionProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public IEventSink<WorkflowRunEventEnvelope>? LastSink { get; private set; }

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            LastSink = sink;
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
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message_id":"reply-1","platform_message_id":"platform-1"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
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
