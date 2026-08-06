using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
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
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowChatConversationContinuationCrossLayerTests : WorkflowGAgentTestBase
{
    [Fact]
    public async Task ContinueConversation_ShouldHydrateCommittedReadModelIntoWorkflowExecutionInputAndIsolateConversationScope()
    {
        var documentStore = new InMemoryProjectionDocumentStore<ChatConversationCurrentStateDocument, string>(
            static document => document.Id);
        var projector = new ChatConversationCurrentStateProjector(
            new DocumentStoreWriteDispatcher(documentStore),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-22T09:00:00Z")));
        await MaterializeCommittedConversationAsync(
            projector,
            documentStore,
            scopeId: "scope-alpha",
            conversationId: "conversation-alpha",
            turnId: "turn-alpha-1",
            userText: "Create a workflow that generates fund analysis reports.",
            assistantText: "Choose a Team: team01 or team02.");
        await MaterializeCommittedConversationAsync(
            projector,
            documentStore,
            scopeId: "scope-alpha",
            conversationId: "conversation-other",
            turnId: "turn-other-1",
            userText: "Other conversation prior prompt.",
            assistantText: "Other conversation prior answer.");
        await MaterializeCommittedConversationAsync(
            projector,
            documentStore,
            scopeId: "scope-beta",
            conversationId: "conversation-alpha",
            turnId: "turn-beta-1",
            userText: "Beta scoped prior prompt.",
            assistantText: "Beta scoped prior answer.");

        using var admissionProvider = BuildAdmissionProvider(documentStore);
        var deliveryRuntime = new DeliveryActorRuntime();
        var deliveryDispatch = new RecordingActorDispatchPort();
        var deliveryPort = new ChatTurnHistoryTerminalDeliveryPort(
            deliveryRuntime,
            deliveryDispatch,
            admissionProvider.GetRequiredService<IChatConversationContinuationAdmissionReader>(),
            NullLogger<ChatTurnHistoryTerminalDeliveryPort>.Instance);

        var alpha = await ExecuteContinuationAsync(
            deliveryPort,
            "scope-alpha",
            "conversation-alpha",
            "team01",
            minimumStateVersion: 1);

        alpha.Result.Succeeded.Should().BeTrue();
        alpha.DispatchedRequest.Should().NotBeNull();
        alpha.DispatchedRequest!.ConversationContext.Should().NotBeNull();
        alpha.DispatchedRequest.ConversationContext!.Messages
            .Select(static message => (message.Sequence, message.Role, message.Content))
            .Should()
            .Equal(
                (1, WorkflowConversationExecutionRole.User, "Create a workflow that generates fund analysis reports."),
                (2, WorkflowConversationExecutionRole.Assistant, "Choose a Team: team01 or team02."));
        var alphaInput = await RenderWorkflowInputAsync(alpha.DispatchedRequest);
        alphaInput.Should().Contain("[user] Create a workflow that generates fund analysis reports.");
        alphaInput.Should().Contain("[assistant] Choose a Team: team01 or team02.");
        alphaInput.Should().Contain("<current_user_message>\nteam01\n</current_user_message>");
        alphaInput.Should().NotContain("[user] team01");
        alphaInput.Should().NotContain("Other conversation prior prompt.");
        alphaInput.Should().NotContain("Beta scoped prior prompt.");

        var staleAlpha = await ExecuteContinuationAsync(
            deliveryPort,
            "scope-alpha",
            "conversation-alpha",
            "team01",
            minimumStateVersion: 2);

        staleAlpha.Result.Succeeded.Should().BeFalse();
        staleAlpha.Result.Error.Should().Be(WorkflowChatRunStartError.ChatHistoryReservationUnavailable);
        staleAlpha.DispatchedRequest.Should().BeNull();

        var sameScopeOtherConversation = await ExecuteContinuationAsync(
            deliveryPort,
            "scope-alpha",
            "conversation-other",
            "team01",
            minimumStateVersion: 1);

        sameScopeOtherConversation.Result.Succeeded.Should().BeTrue();
        var sameScopeOtherInput = await RenderWorkflowInputAsync(sameScopeOtherConversation.DispatchedRequest!);
        sameScopeOtherInput.Should().Contain("Other conversation prior prompt.");
        sameScopeOtherInput.Should().NotContain("Create a workflow that generates fund analysis reports.");
        sameScopeOtherInput.Should().NotContain("Beta scoped prior prompt.");

        var otherScopeSameConversationId = await ExecuteContinuationAsync(
            deliveryPort,
            "scope-beta",
            "conversation-alpha",
            "team01",
            minimumStateVersion: 1);

        otherScopeSameConversationId.Result.Succeeded.Should().BeTrue();
        var otherScopeInput = await RenderWorkflowInputAsync(otherScopeSameConversationId.DispatchedRequest!);
        otherScopeInput.Should().Contain("Beta scoped prior prompt.");
        otherScopeInput.Should().NotContain("Create a workflow that generates fund analysis reports.");
        otherScopeInput.Should().NotContain("Other conversation prior prompt.");
    }

    private static async Task MaterializeCommittedConversationAsync(
        ChatConversationCurrentStateProjector projector,
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> documentReader,
        string scopeId,
        string conversationId,
        string turnId,
        string userText,
        string assistantText)
    {
        var actorId = ChatHistoryActorIds.Conversation(scopeId, conversationId);
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = await CreateConversationAgentAsync(actorId, eventStore, publisher);

        await agent.HandleEventAsync(DirectEnvelope(actorId, new AppendChatTurnCommand
        {
            ScopeId = scopeId,
            ConversationId = conversationId,
            Turn = new ChatTurn
            {
                TurnId = turnId,
                UserText = userText,
                AssistantText = assistantText,
                TerminalStatus = ChatTurnTerminalStatus.Completed,
                TerminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-22T08:00:00Z")),
                LlmRoute = "route-a",
                LlmModel = "model-a",
            },
        }));

        var persisted = await eventStore.GetEventsAsync(actorId);
        var appended = persisted.Should()
            .ContainSingle(static stateEvent => stateEvent.EventData.Is(ChatTurnAppendedEvent.Descriptor))
            .Subject;
        var committed = publisher.Published
            .Select(static publication => publication.evt)
            .OfType<CommittedStateEventPublished>()
            .Should()
            .ContainSingle(publication => publication.StateEvent.EventId == appended.EventId)
            .Subject;

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = actorId,
                ProjectionKind = ChatConversationGAgent.ProjectionKind,
            },
            new EventEnvelope
            {
                Id = appended.EventId,
                Timestamp = appended.Timestamp?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId),
                Payload = Any.Pack(committed),
            });

        var document = await documentReader.GetAsync(actorId);
        document.Should().NotBeNull();
        document!.StateVersion.Should().Be(appended.Version);
    }

    private static async Task<ChatConversationGAgent> CreateConversationAgentAsync(
        string actorId,
        IEventStore eventStore,
        RecordingEventPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, RecordingActorDispatchPort>()
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

    private static ServiceProvider BuildAdmissionProvider(
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> documentReader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(documentReader);
        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static async Task<ContinuationExecution> ExecuteContinuationAsync(
        IWorkflowChatHistoryTerminalDeliveryPort deliveryPort,
        string scopeId,
        string conversationId,
        string prompt,
        long? minimumStateVersion = null)
    {
        var inner = new RecordingWorkflowInteractionService();
        var service = new WorkflowChatRunInteractionService(
            new SequencedActorResolver(),
            new NoopWorkflowExecutionProjectionPort(),
            new NoopWorkflowRunProvisioningPort(),
            inner,
            new WorkflowDirectFallbackPolicy(),
            deliveryPort);

        var result = await service.ExecuteAsync(
            new WorkflowChatRunRequest(
                prompt,
                WorkflowChatSource.CatalogWorkflow("direct"),
                ExternalCapabilityExecutionMode.Interactive,
                ScopeId: scopeId,
                ChatConversation: WorkflowChatConversationIntent.Continue(
                    conversationId,
                    minimumStateVersion)),
            static (_, _) => ValueTask.CompletedTask);

        return new ContinuationExecution(result, inner.Requests.SingleOrDefault());
    }

    private static async Task<string> RenderWorkflowInputAsync(WorkflowChatRunRequest request)
    {
        var envelope = new WorkflowChatRequestEnvelopeFactory().CreateEnvelope(
            request,
            new CommandContext(
                request.CommandIdSeed ?? "cmd-render",
                request.CorrelationIdSeed ?? "corr-render",
                request.TargetSeed?.ActorId ?? "run-render",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
            "definition-render",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: request.TargetSeed?.ActorId ?? "run-render");

        await agent.HandleChatRequest(payload);

        return publisher.Published
            .Select(static publication => publication.evt)
            .OfType<StartWorkflowEvent>()
            .Should()
            .ContainSingle()
            .Subject
            .Input;
    }

    private static EventEnvelope DirectEnvelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
        };

    private sealed record ContinuationExecution(
        WorkflowChatRunInteractionResult Result,
        WorkflowChatRunRequest? DispatchedRequest);

    private sealed class DocumentStoreWriteDispatcher(
        IProjectionDocumentWriter<ChatConversationCurrentStateDocument> writer)
        : IProjectionWriteDispatcher<ChatConversationCurrentStateDocument>
    {
        public Task<ProjectionWriteResult> UpsertAsync(
            ChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default) =>
            writer.UpsertAsync(readModel, ct);

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            writer.DeleteAsync(id, ct);
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
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

    private sealed class DeliveryActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? $"{agentType.Name}-{Guid.NewGuid():N}";
            _existing.Add(actorId);
            return Task.FromResult<IActor>(new NoopActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_existing.Contains(id) ? new NoopActor(id) : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_existing.Contains(id));

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
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class SequencedActorResolver : IWorkflowRunActorResolver
    {
        private int _next;

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var sequence = Interlocked.Increment(ref _next);
            return Task.FromResult(new WorkflowActorResolutionResult(
                new WorkflowRunCreationReceipt(
                    $"run-{sequence}",
                    $"definition-{sequence}",
                    [$"definition-{sequence}", $"run-{sequence}"]),
                request.Source.WorkflowName ?? "direct",
                WorkflowChatRunStartError.None));
        }
    }

    private sealed class NoopWorkflowExecutionProjectionPort : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(IWorkflowExecutionProjectionLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopWorkflowRunProvisioningPort : IWorkflowRunProvisioningPort
    {
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingWorkflowInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Requests.Add(command);
            var receipt = new WorkflowChatRunAcceptedReceipt(
                command.TargetSeed?.ActorId ?? "run-accepted",
                command.TargetSeed?.WorkflowNameForRun ?? command.Source.WorkflowName ?? "direct",
                command.CommandIdSeed ?? "cmd-accepted",
                command.CorrelationIdSeed ?? "corr-accepted");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        }

        async Task<RealtimeSessionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>
            IRealtimeSession<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>.ExecuteAsync(
                WorkflowChatRunRequest inbound,
                Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
                Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }
}
