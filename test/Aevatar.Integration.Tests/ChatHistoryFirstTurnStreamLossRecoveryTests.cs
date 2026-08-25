using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

/// <summary>
/// Regression contract for aevatar#3179: the first turn must not be
/// stream-or-nothing. No SSE observer exists anywhere in this harness — the
/// client connection is gone right after the context frames — yet the
/// acknowledged conversationId must read 200 (pending) from the moment the
/// reservation is committed, and the committed reply must become retrievable
/// from chat-history once the run reaches terminal server-side.
/// </summary>
public sealed class ChatHistoryFirstTurnStreamLossRecoveryTests : WorkflowGAgentTestBase
{
    private const string ScopeId = "scope-first-turn";
    private const string WorkflowActorId = "workflow-definition:studio:run:first-turn";
    private const string WorkflowCommandId = "create-command-first-turn";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-08-04T01:37:02Z");

    [Fact]
    public async Task FirstTurn_WithClientStreamGoneAfterAcknowledgement_ShouldReadPendingThenCommittedReply()
    {
        var conversationId = ChatHistoryActorIds.CreateConversationId(ScopeId, WorkflowCommandId);
        var turnId = ChatHistoryActorIds.CreateTurnId(ScopeId, WorkflowCommandId);
        var deliveryId = ChatHistoryActorIds.TurnDelivery(WorkflowActorId, WorkflowCommandId);
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        var conversationActorId = ChatHistoryActorIds.Conversation(ScopeId, conversationId);

        var conversationDocuments =
            new InMemoryProjectionDocumentStore<ChatConversationCurrentStateDocument, string>(
                static document => document.Id);
        var recoveryDocuments =
            new InMemoryProjectionDocumentStore<ChatHistoryCreateRecoveryCurrentStateDocument, string>(
                static document => document.Id);
        var recoveryProjector = new ChatHistoryCreateRecoveryCurrentStateProjector(
            new StoreWriteDispatcher<ChatHistoryCreateRecoveryCurrentStateDocument>(recoveryDocuments),
            new FixedProjectionClock(FixedNow));
        var conversationProjector = new ChatConversationCurrentStateProjector(
            new StoreWriteDispatcher<ChatConversationCurrentStateDocument>(conversationDocuments),
            new FixedProjectionClock(FixedNow));
        using var queryProvider = BuildChatHistoryQueryProvider(
            conversationDocuments,
            recoveryDocuments);
        var queryPort = queryProvider.GetRequiredService<IChatHistoryQueryPort>();

        // ── Acceptance: the delivery actor commits the create reservation. This
        // is the committed fact behind the `aevatar.chat.context` acknowledgement.
        var deliveryPublisher = new RecordingEventPublisher();
        var deliveryDispatch = new CapturingActorDispatchPort();
        var deliveryAgent = await CreateDeliveryAgentAsync(deliveryPublisher, deliveryDispatch);
        await deliveryAgent.HandleEventAsync(DeliveryEnvelope(
            new ChatTurnHistoryDeliveryReserveRequested
            {
                DeliveryId = deliveryId,
                ScopeId = ScopeId,
                ConversationId = conversationId,
                TurnId = turnId,
                UserText = "hi",
                SourceActorId = WorkflowActorId,
                SourceCommandId = WorkflowCommandId,
                SourceCorrelationId = "correlation-first-turn",
                CreateConversationIfMissing = true,
                ExposeCreateRecovery = true,
                RequestFingerprint = "fingerprint-first-turn",
            },
            "chat-history-terminal-delivery-port",
            deliveryActorId));
        await ProjectDeliveryCommitsAsync(deliveryPublisher, recoveryProjector, deliveryActorId);

        // The stream is dead from this point on. The acknowledged id must read
        // 200-pending, never 404.
        var acknowledged = await queryPort.GetMessagesAsync(ScopeId, conversationId);
        acknowledged.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
        acknowledged.ProjectionStatus.Should().Be(ChatHistoryConversationProjectionStatus.Pending);
        acknowledged.StateVersion.Should().Be(0);
        acknowledged.Messages.Should().BeEmpty();

        // 404 stays reserved for authoritative absence.
        var neverCreated = await queryPort.GetMessagesAsync(ScopeId, "conversation-never-created");
        neverCreated.Status.Should().Be(ChatHistoryConversationResultStatus.NotFound);

        // ── The run completes server-side without any client connection.
        await deliveryAgent.HandleEventAsync(DeliveryEnvelope(
            new ChatTurnHistoryDeliveryAcceptedBound
            {
                DeliveryId = deliveryId,
                SourceActorId = WorkflowActorId,
                SourceCommandId = WorkflowCommandId,
                SourceCorrelationId = "correlation-first-turn",
            },
            "chat-history-terminal-delivery-port",
            deliveryActorId));
        await deliveryAgent.HandleEventAsync(DeliveryEnvelope(
            new WorkflowRunTerminalNotification
            {
                DeliveryId = deliveryId,
                WorkflowActorId = WorkflowActorId,
                WorkflowRunId = "workflow-run-first-turn",
                WorkflowCommandId = WorkflowCommandId,
                WorkflowCorrelationId = "correlation-first-turn",
                Status = WorkflowRunTerminalStatus.Completed,
                Output = "The committed reply.",
                TerminalAt = Timestamp.FromDateTimeOffset(FixedNow.AddSeconds(3)),
            },
            WorkflowActorId,
            deliveryActorId));
        await ProjectDeliveryCommitsAsync(deliveryPublisher, recoveryProjector, deliveryActorId);

        // Projection-lag window: terminal reached and the append dispatched, but
        // the conversation read model has not caught up yet. Still pending, not 404.
        var lagWindow = await queryPort.GetMessagesAsync(ScopeId, conversationId);
        lagWindow.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
        lagWindow.ProjectionStatus.Should().Be(ChatHistoryConversationProjectionStatus.Pending);

        // ── The delivery actor appends the terminal turn to the conversation actor.
        var appendCall = deliveryDispatch.Calls.Should().ContainSingle().Subject;
        appendCall.ActorId.Should().Be(conversationActorId);
        appendCall.Envelope.Payload.Is(AppendChatTurnCommand.Descriptor).Should().BeTrue();

        var conversationPublisher = new RecordingEventPublisher();
        var conversationDispatch = new CapturingActorDispatchPort();
        var conversationAgent = await CreateConversationAgentAsync(
            conversationActorId,
            conversationPublisher,
            conversationDispatch);
        await conversationAgent.HandleEventAsync(appendCall.Envelope);
        await ProjectConversationCommitsAsync(
            conversationPublisher,
            conversationProjector,
            conversationActorId);

        // The committed reply is retrievable from chat-history without the
        // original socket; a polling client converges on it.
        var populated = await queryPort.GetMessagesAsync(ScopeId, conversationId);
        populated.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
        populated.ProjectionStatus.Should().Be(ChatHistoryConversationProjectionStatus.Current);
        populated.StateVersion.Should().Be(1);
        populated.Messages.Select(static message => (message.Role, message.Content))
            .Should()
            .Equal(("user", "hi"), ("assistant", "The committed reply."));

        // The append result closes the delivery loop.
        var appendResult = conversationDispatch.Calls.Should().ContainSingle().Subject;
        appendResult.ActorId.Should().Be(deliveryActorId);
        await deliveryAgent.HandleEventAsync(appendResult.Envelope);
        deliveryAgent.State.Status.Should().Be(ChatTurnHistoryDeliveryStatus.AppendCommitted);
    }

    private static async Task<ChatTurnHistoryDeliveryGAgent> CreateDeliveryAgentAsync(
        RecordingEventPublisher publisher,
        CapturingActorDispatchPort dispatch)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(new InMemoryEventStore())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new ChatTurnHistoryDeliveryGAgent(
            new NoopActorRuntime(),
            dispatch,
            NullLogger<ChatTurnHistoryDeliveryGAgent>.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatTurnHistoryDeliveryState>>(),
            CommittedStateEventPublisher = publisher,
        };
        SetAgentId(agent, ChatTurnHistoryDeliveryActorIds.FromDeliveryId(
            ChatHistoryActorIds.TurnDelivery(WorkflowActorId, WorkflowCommandId)));
        await agent.ActivateAsync();
        return agent;
    }

    private static async Task<ChatConversationGAgent> CreateConversationAgentAsync(
        string actorId,
        RecordingEventPublisher publisher,
        CapturingActorDispatchPort dispatch)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(new InMemoryEventStore())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new ChatConversationGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatConversationState>>(),
            CommittedStateEventPublisher = publisher,
        };
        SetAgentId(agent, actorId);
        await agent.ActivateAsync();
        return agent;
    }

    private static async Task ProjectDeliveryCommitsAsync(
        RecordingEventPublisher publisher,
        ChatHistoryCreateRecoveryCurrentStateProjector projector,
        string deliveryActorId)
    {
        foreach (var committed in DrainCommitted(publisher))
        {
            await projector.ProjectAsync(
                new StudioMaterializationContext
                {
                    RootActorId = deliveryActorId,
                    ProjectionKind = ChatTurnHistoryDeliveryGAgent.ProjectionKind,
                },
                CommittedEnvelope(committed, deliveryActorId));
        }
    }

    private static async Task ProjectConversationCommitsAsync(
        RecordingEventPublisher publisher,
        ChatConversationCurrentStateProjector projector,
        string conversationActorId)
    {
        foreach (var committed in DrainCommitted(publisher))
        {
            await projector.ProjectAsync(
                new StudioMaterializationContext
                {
                    RootActorId = conversationActorId,
                    ProjectionKind = ChatConversationGAgent.ProjectionKind,
                },
                CommittedEnvelope(committed, conversationActorId));
        }
    }

    private static IReadOnlyList<CommittedStateEventPublished> DrainCommitted(
        RecordingEventPublisher publisher)
    {
        var committed = publisher.Published
            .Select(static publication => publication.evt)
            .OfType<CommittedStateEventPublished>()
            .Select(static publication => publication.Clone())
            .ToList();
        publisher.Published.Clear();
        return committed;
    }

    private static EventEnvelope CommittedEnvelope(
        CommittedStateEventPublished committed,
        string rootActorId)
    {
        committed.StateEvent.Timestamp ??= Timestamp.FromDateTimeOffset(FixedNow);
        return new EventEnvelope
        {
            Id = committed.StateEvent.EventId,
            Timestamp = committed.StateEvent.Timestamp.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(rootActorId),
            Payload = Any.Pack(committed),
        };
    }

    private static EventEnvelope DeliveryEnvelope(
        IMessage payload,
        string publisherActorId,
        string deliveryActorId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, deliveryActorId),
        };

    private static ServiceProvider BuildChatHistoryQueryProvider(
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> conversationDocuments,
        IProjectionDocumentReader<ChatHistoryCreateRecoveryCurrentStateDocument, string> recoveryDocuments)
    {
        var services = new ServiceCollection();
        services.AddSingleton(conversationDocuments);
        services.AddSingleton(recoveryDocuments);
        services.AddSingleton<IActorRuntime, NoopActorRuntime>();
        services.AddSingleton<IActorDispatchPort, CapturingActorDispatchPort>();
        services.AddSingleton<IStudioActorBootstrap, NoopBootstrap>();
        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private sealed class StoreWriteDispatcher<TDocument>(
        IProjectionDocumentWriter<TDocument> writer)
        : IProjectionWriteDispatcher<TDocument>
        where TDocument : class, IProjectionReadModel
    {
        public Task<ProjectionWriteResult> UpsertAsync(TDocument readModel, CancellationToken ct = default) =>
            writer.UpsertAsync(readModel, ct);

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            writer.DeleteAsync(id, ct);
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class CapturingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class NoopActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IActor>(new NoopActor(id ?? agentType.Name));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent(id);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopBootstrap : IStudioActorBootstrap
    {
        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult<IActor>(new NoopActor(actorId));
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

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

}
