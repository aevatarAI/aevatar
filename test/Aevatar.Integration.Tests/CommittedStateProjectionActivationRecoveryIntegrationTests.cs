using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgents.ChatHistory;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class CommittedStateProjectionActivationRecoveryIntegrationTests : WorkflowGAgentTestBase
{
    private const string ActorId = "chat-history-activation-recovery";
    private const string ProjectionKind = "activation-recovery";
    private const string ScopeAgentKind = "projection.materialization-scope.recovery-context";

    [Fact]
    public async Task RelayReadinessFailure_ShouldLeavePublicationUnconfirmed_AndRecoverSameEnvelopeIdentity()
    {
        var eventStore = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        var streams = new InMemoryStreamProvider();
        var runtime = new CountingRuntime();
        var dispatch = new CountingDispatchPort();
        var kindRegistry = new AgentKindRegistry(
        [
            new AgentRegistration(
                ScopeAgentKind,
                typeof(ProjectionMaterializationScopeGAgent<RecoveryContext>),
                typeof(ProjectionScopeState)),
        ]);
        kindRegistry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<RecoveryContext>),
                out var scopeAgentKind)
            .Should().BeTrue();
        var authority = new FailOnceRelayReadinessAuthority(scopeAgentKind);
        var activation = new ProjectionScopeActivationService<
            RecoveryLease,
            RecoveryContext,
            ProjectionMaterializationScopeGAgent<RecoveryContext>>(
            runtime,
            dispatch,
            request => new RecoveryContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            },
            (_, context) => new RecoveryLease(context),
            agentKindRegistry: kindRegistry,
            bindingAuthority: authority);

        using var services = BuildServices(
            eventStore,
            publicationStore,
            streams,
            activation);
        var publisher = new LocalActorPublisher(ActorId, () => null, () => 0, streams);
        var acceptedEnvelope = new TaskCompletionSource<EventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await streams.GetStream(ActorId).SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
                acceptedEnvelope.TrySetResult(envelope.Clone());
            return Task.CompletedTask;
        });

        var first = await CreateAgentAsync(services, publisher);
        var failure = await FluentActions.Invoking(() => first.HandleInitializeChatConversation(Command()))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        failure.Which.Stage.Should().Be(CommittedStatePublicationFailureStage.AdapterAcceptance);
        acceptedEnvelope.Task.IsCompleted.Should().BeFalse();
        var committed = (await eventStore.GetEventsAsync(ActorId)).Should().ContainSingle().Subject;
        var failedCheckpoint = await publicationStore.LoadAsync(ActorId);
        failedCheckpoint.Should().NotBeNull();
        failedCheckpoint!.PublishedVersion.Should().Be(0);
        failedCheckpoint.Failure.Should().NotBeNull();
        failedCheckpoint.Failure!.EventId.Should().Be(committed.EventId);
        failedCheckpoint.Failure.Stage.Should().Be(CommittedStatePublicationFailureStage.AdapterAcceptance);

        var recovered = await CreateAgentAsync(services, publisher);
        var envelope = await acceptedEnvelope.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();

        recovered.State.Initialization.Should().NotBeNull();
        envelope.Id.Should().Be(committed.EventId);
        published.StateEvent.EventId.Should().Be(committed.EventId);
        published.StateEvent.Version.Should().Be(committed.Version);
        var recoveredCheckpoint = await publicationStore.LoadAsync(ActorId);
        recoveredCheckpoint.Should().NotBeNull();
        recoveredCheckpoint!.PublishedVersion.Should().Be(committed.Version);
        recoveredCheckpoint.PublishedEventId.Should().Be(committed.EventId);
        recoveredCheckpoint.Failure.Should().BeNull();
        authority.ReadCount.Should().Be(4);
        runtime.CreateCallCount.Should().Be(1);
        dispatch.CallCount.Should().Be(1);
    }

    private static ServiceProvider BuildServices(
        IEventStore eventStore,
        ICommittedStatePublicationStateStore publicationStore,
        IStreamProvider streams,
        IProjectionScopeActivationService<RecoveryLease> activation)
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<ICommittedStatePublicationStateStore>(publicationStore)
            .AddSingleton<IStreamProvider>(streams)
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IProjectionScopeActivationService<RecoveryLease>>(activation)
            .AddSingleton<IProjectionActivationPlanProvider, RecoveryPlanProvider>()
            .AddSingleton<ProjectionActivationPlanDispatcher>()
            .AddSingleton<ICommittedStatePublicationHook, CommittedStateProjectionActivationHook>()
            .BuildServiceProvider();
    }

    private static async Task<ChatConversationGAgent> CreateAgentAsync(
        ServiceProvider services,
        ICommittedStateEventPublisher publisher)
    {
        var agent = new ChatConversationGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatConversationState>>(),
            CommittedStateEventPublisher = publisher,
        };
        SetAgentId(agent, ActorId);
        await agent.ActivateAsync();
        return agent;
    }

    private static InitializeChatConversationCommand Command() => new()
    {
        OperationId = "operation-1",
        ScopeId = "scope-alpha",
        ConversationId = "conversation-alpha",
        ServiceId = "service-alpha",
        ServiceKind = "workflow",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-17T00:00:00Z")),
        InitialTitle = "Activation recovery",
    };

    private sealed class RecoveryPlanProvider : IProjectionActivationPlanProvider
    {
        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context) =>
        [
            new ProjectionActivationPlan
            {
                LeaseType = typeof(RecoveryLease),
                StartRequest = new ProjectionScopeStartRequest
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = ProjectionKind,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
            },
        ];
    }

    private sealed class RecoveryContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;
        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class RecoveryLease(RecoveryContext context)
        : ProjectionRuntimeLeaseBase(context.RootActorId), IProjectionContextRuntimeLease<RecoveryContext>
    {
        public RecoveryContext Context { get; } = context;
    }

    private sealed class FailOnceRelayReadinessAuthority(string scopeAgentKind)
        : IStreamForwardingBindingAuthority
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _readCount) switch
            {
                1 => Task.FromResult<StreamForwardingBinding?>(null),
                2 => Task.FromResult<StreamForwardingBinding?>(null),
                3 => Task.FromException<StreamForwardingBinding?>(
                    new InvalidOperationException("Injected relay readiness failure.")),
                _ => Task.FromResult<StreamForwardingBinding?>(
                    new StreamForwardingBinding
                    {
                        SourceStreamId = sourceStreamId,
                        TargetStreamId = targetStreamId,
                        ForwardingMode = StreamForwardingMode.HandleThenForward,
                        DirectionFilter = [],
                        EventTypeFilter =
                        [
                            $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}",
                        ],
                        TargetActorKind = scopeAgentKind,
                        ActivationGeneration = 1,
                    }),
            };
        }
    }

    private sealed class CountingDispatchPort : IActorDispatchPort
    {
        public int CallCount { get; private set; }

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class CountingRuntime : IActorRuntime
    {
        private bool _exists;

        public int CreateCallCount { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor> CreateByKindAsync(
            string agentKind,
            string? id = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateCallCount++;
            _exists = true;
            return Task.FromResult<IActor>(new TestActor(id!));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_exists);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
