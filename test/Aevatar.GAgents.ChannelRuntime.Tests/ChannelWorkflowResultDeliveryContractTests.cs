using System.Net;
using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Cross-module contract gate for channel workflow result delivery (#2675): a vault handle
/// minted by Lark provisioning must flow through the registration store entry into the
/// delivery credential the turn runner exposes, and that same handle must resolve inside
/// <see cref="WorkflowRunDeliveryGAgent"/> so the workflow terminal result reaches the chat
/// with the NyxID full_key as bearer. This is the gate the previously contradictory
/// provisioning and delivery unit tests were missing.
/// </summary>
public sealed class ChannelWorkflowResultDeliveryContractTests
{
    private const string DeliveryActorId = "workflow-run-delivery:workflow-actor:wf-command";

    [Fact]
    public async Task ProvisionedHandle_ShouldDeliverWorkflowTerminalResultWithVaultResolvedFullKey()
    {
        // 1. Provision a Lark bot; NyxID returns the one-time full_key exactly once.
        var secretVault = new InMemorySecretVault();
        var provisioningHandler = new QueueHandler();
        provisioningHandler.Enqueue("""{"id":"key-123","full_key":"nyxid_ag_full_key_1"}""");
        provisioningHandler.Enqueue("""{"id":"bot-456","status":"pending_webhook"}""");
        provisioningHandler.Enqueue("""{"id":"route-789","default_agent":true}""");
        provisioningHandler.Enqueue("""{"id":"svc-1"}""");

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var provisioningService = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(provisioningHandler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime),
            secretVault,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NyxLarkProvisioningService>>());

        var provisioningResult = await provisioningService.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: "user-token",
                AppId: "cli_a1b2c3",
                AppSecret: "secret-xyz",
                VerificationToken: string.Empty,
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-lark-bot"),
            CancellationToken.None);
        provisioningResult.Succeeded.Should().BeTrue();
        provisioningResult.WorkflowResultDeliveryEnabled.Should().BeTrue();

        // 2. The local mirror command lands in the registration store actor.
        var registrationAgent = await CreateRegistrationAgentAsync();
        await registrationAgent.HandleRegister(
            capturedEnvelope!.Payload.Unpack<ChannelBotRegisterCommand>());
        var entry = registrationAgent.State.Registrations.Should().ContainSingle().Subject;
        entry.WorkflowResultDeliveryCredential.Should().NotBeNull();
        entry.NyxAgentApiKeyId.Should().Be("key-123");

        // 3. Compose the delivery credential exactly like ChannelConversationTurnRunner does:
        //    the persisted vault SecretReference plus the api-key subject it authorizes.
        var deliveryCredential = new ChannelWorkflowResultDeliveryCredential
        {
            SecretReference = entry.WorkflowResultDeliveryCredential.Clone(),
            SubjectId = entry.NyxAgentApiKeyId,
        };

        // 4. The delivery actor accepts the start, observes the workflow terminal frame, and
        //    replies through the relay with the vault-resolved full_key as bearer.
        var projectionPort = new RecordingWorkflowExecutionProjectionPort();
        var relayHandler = new QueueHandler();
        relayHandler.Enqueue("""{"message_id":"reply-1","platform_message_id":"platform-1"}""");
        var deferredDispatchPort = new DeferredDispatchPort();
        var deliveryAgent = await CreateDeliveryAgentAsync(
            projectionPort,
            CreateOutboundPort(relayHandler),
            deferredDispatchPort,
            new SecretVaultWorkflowResultDeliveryCredentialResolver(secretVault));
        deferredDispatchPort.Inner = new DirectActorDispatchPort(deliveryAgent);

        await deliveryAgent.HandleEventAsync(Envelope(new WorkflowRunDeliveryStartRequested
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
            RegistrationScopeId = "scope-1",
            WorkflowResultDeliveryCredential = deliveryCredential,
            BotRegistrationId = entry.Id,
        }));
        deliveryAgent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);

        await projectionPort.LastSink!.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "invoice approved" }),
            },
        });

        deliveryAgent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer nyxid_ag_full_key_1");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"invoice approved\"");
    }

    private static async Task<ChannelBotRegistrationGAgent> CreateRegistrationAgentAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new ChannelBotRegistrationGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(agent, ChannelBotRegistrationGAgent.WellKnownId);
        await agent.ActivateAsync();
        return agent;
    }

    private static async Task<WorkflowRunDeliveryGAgent> CreateDeliveryAgentAsync(
        RecordingWorkflowExecutionProjectionPort projectionPort,
        NyxIdRelayOutboundPort outboundPort,
        DeferredDispatchPort dispatchPort,
        IWorkflowResultDeliveryCredentialResolver credentialResolver)
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
            credentialResolver,
            NullLogger<WorkflowRunDeliveryGAgent>.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDeliveryGAgentState>>(),
        };
        SetId(agent, DeliveryActorId);
        await agent.ActivateAsync();
        return agent;
    }

    private static void SetId(GAgentBase agent, string actorId) =>
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

    private static NyxIdRelayOutboundPort CreateOutboundPort(HttpMessageHandler handler) =>
        new(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") },
                NullLogger<NyxIdApiClient>.Instance),
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new PlainTextComposer("lark")]);

    private static EventEnvelope Envelope<T>(T payload)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", DeliveryActorId),
        };

    private sealed class DeferredDispatchPort : IActorDispatchPort
    {
        public IActorDispatchPort? Inner { get; set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            (Inner ?? throw new InvalidOperationException("Dispatch port is not bound."))
            .DispatchAsync(actorId, envelope, ct);
    }

    private sealed class DirectActorDispatchPort(WorkflowRunDeliveryGAgent agent) : IActorDispatchPort
    {
        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            await agent.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
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
                new(new ProjectionLease(rootActorId, commandId), new NoopAsyncDisposable()));
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

    private sealed record ProjectionLease(string ActorId, string CommandId) : IWorkflowExecutionProjectionLease;

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new();

        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        public void Enqueue(string body) => _responses.Enqueue(body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
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
