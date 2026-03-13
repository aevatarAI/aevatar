using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunDetachedCleanupOutboxTests
{
    private static IServiceProvider CreateAgentServices(
        IEventStore? eventStore = null,
        IWorkflowExecutionProjectionQueryPort? queryPort = null,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>? lifecycle = null,
        IWorkflowProjectionReadModelUpdater? readModelUpdater = null,
        IProjectionOwnershipCoordinator? ownershipCoordinator = null,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent>? projectionControlHub = null,
        IWorkflowRunActorPort? actorPort = null,
        WorkflowExecutionProjectionOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore ?? new InMemoryEventStore());
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton(options ?? new WorkflowExecutionProjectionOptions
        {
            DetachedCleanupRetryBaseDelayMs = 0,
            DetachedCleanupRetryMaxDelayMs = 0,
        });
        services.AddSingleton<IWorkflowExecutionProjectionQueryPort>(queryPort ?? new RecordingQueryPort());
        services.AddSingleton<IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>(
            lifecycle ?? new RecordingLifecycleService());
        services.AddSingleton<IWorkflowExecutionProjectionContextFactory, DefaultWorkflowExecutionProjectionContextFactory>();
        services.AddSingleton<IWorkflowProjectionReadModelUpdater>(readModelUpdater ?? new RecordingReadModelUpdater());
        services.AddSingleton<IProjectionOwnershipCoordinator>(ownershipCoordinator ?? new RecordingOwnershipCoordinator());
        services.AddSingleton<IProjectionSessionEventHub<WorkflowProjectionControlEvent>>(projectionControlHub ?? new RecordingProjectionControlHub());
        services.AddSingleton<IWorkflowRunActorPort>(actorPort ?? new RecordingActorPort());
        return services.BuildServiceProvider();
    }

    private static WorkflowRunDetachedCleanupOutboxGAgent CreateAgent(IServiceProvider services) =>
        new()
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDetachedCleanupOutboxState>>(),
        };

    [Fact]
    public void BuildActorIdAndRecordId_ShouldNormalizeAndValidate()
    {
        WorkflowRunDetachedCleanupOutboxGAgent.BuildActorId(" workflow ").Should()
            .Be("workflow.run.detached.cleanup.outbox:workflow");
        WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(" actor-1 ", " cmd-1 ").Should()
            .Be("actor-1::cmd-1");

        Action actOnScope = () => WorkflowRunDetachedCleanupOutboxGAgent.BuildActorId(" ");
        Action actOnActor = () => WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(" ", "cmd-1");
        Action actOnCommand = () => WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId("actor-1", " ");

        actOnScope.Should().Throw<ArgumentException>();
        actOnActor.Should().Throw<ArgumentException>();
        actOnCommand.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenSnapshotIsTerminal_ShouldReleaseOriginalProjectionLeaseAndDestroyActors()
    {
        var queryPort = new RecordingQueryPort
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "actor-1",
                WorkflowName = "direct",
                CompletionStatus = WorkflowRunCompletionStatus.Completed,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        var lifecycle = new RecordingLifecycleService();
        var projectionControlHub = new RecordingProjectionControlHub();
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownershipCoordinator = new RecordingOwnershipCoordinator();
        var actorPort = new RecordingActorPort();
        var agent = CreateAgent(CreateAgentServices(
            queryPort: queryPort,
            lifecycle: lifecycle,
            readModelUpdater: readModelUpdater,
            ownershipCoordinator: ownershipCoordinator,
            projectionControlHub: projectionControlHub,
            actorPort: actorPort));
        var streamSubscriptionLease = new RecordingStreamSubscriptionLease("actor-1");
        var originalContext = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
            StreamSubscriptionLease = streamSubscriptionLease,
        };
        var runtimeLease = new WorkflowExecutionRuntimeLease(
            originalContext,
            lifecycle: lifecycle,
            projectionControlHub: projectionControlHub);
        await projectionControlHub.SubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await agent.HandleEnqueueAsync(CreateEnqueueEvent(
            "actor-1",
            "direct",
            "cmd-1",
            ["definition-1", "actor-1"]));
        await agent.HandleTriggerReplayAsync(new WorkflowRunDetachedCleanupTriggerReplayEvent { BatchSize = 10 });
        await lifecycle.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var entry = agent.State.Entries.Values.Single();
        entry.CompletedAtUtc.Should().NotBeNull();
        lifecycle.StopCalls.Should().ContainSingle();
        lifecycle.StopCalls.Single().Should().BeSameAs(originalContext);
        streamSubscriptionLease.DisposeCalls.Should().Be(1);
        ownershipCoordinator.AcquireCalls.Should().ContainSingle().Which.Should().Be(("actor-1", "cmd-1"));
        readModelUpdater.MarkStoppedActorIds.Should().ContainSingle().Which.Should().Be("actor-1");
        ownershipCoordinator.ReleaseCalls.Should().ContainSingle().Which.Should().Be(("actor-1", "cmd-1"));
        actorPort.DestroyCalls.Should().Equal("actor-1", "definition-1");

        await runtimeLease.StopProjectionReleaseListenerAsync();
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenSnapshotIsStillRunning_ShouldLeaveEntryPending()
    {
        var queryPort = new RecordingQueryPort
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "actor-1",
                WorkflowName = "direct",
                CompletionStatus = WorkflowRunCompletionStatus.Running,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        var lifecycle = new RecordingLifecycleService();
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownershipCoordinator = new RecordingOwnershipCoordinator();
        var actorPort = new RecordingActorPort();
        var agent = CreateAgent(CreateAgentServices(
            queryPort: queryPort,
            lifecycle: lifecycle,
            readModelUpdater: readModelUpdater,
            ownershipCoordinator: ownershipCoordinator,
            actorPort: actorPort));

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("actor-1", "direct", "cmd-1", ["definition-1"]));
        await agent.HandleTriggerReplayAsync(new WorkflowRunDetachedCleanupTriggerReplayEvent { BatchSize = 10 });

        var entry = agent.State.Entries.Values.Single();
        entry.CompletedAtUtc.Should().BeNull();
        entry.AttemptCount.Should().Be(0);
        lifecycle.StopCalls.Should().BeEmpty();
        readModelUpdater.MarkStoppedActorIds.Should().BeEmpty();
        ownershipCoordinator.AcquireCalls.Should().ContainSingle().Which.Should().Be(("actor-1", "cmd-1"));
        ownershipCoordinator.ReleaseCalls.Should().BeEmpty();
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenQueryThrows_ShouldScheduleRetryWithBackoff()
    {
        var queryPort = new RecordingQueryPort
        {
            Exception = new InvalidOperationException("query failed"),
        };
        var options = new WorkflowExecutionProjectionOptions
        {
            DetachedCleanupRetryBaseDelayMs = 200,
            DetachedCleanupRetryMaxDelayMs = 1000,
        };
        var agent = CreateAgent(CreateAgentServices(queryPort: queryPort, options: options));

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("actor-1", "direct", "cmd-1", ["definition-1"]));
        await agent.HandleTriggerReplayAsync(new WorkflowRunDetachedCleanupTriggerReplayEvent { BatchSize = 10 });
        var afterReplay = DateTime.UtcNow;

        var entry = agent.State.Entries.Values.Single();
        entry.AttemptCount.Should().Be(1);
        entry.CompletedAtUtc.Should().BeNull();
        entry.LastError.Should().Contain("query failed");
        entry.NextVisibleAtUtc.Should().NotBeNull();
        var nextVisible = entry.NextVisibleAtUtc!.ToDateTime();
        if (nextVisible.Kind != DateTimeKind.Utc)
            nextVisible = DateTime.SpecifyKind(nextVisible, DateTimeKind.Utc);
        nextVisible.Should().BeAfter(afterReplay);
    }

    [Fact]
    public async Task State_ShouldSurviveDeactivateAndReactivate()
    {
        var store = new InMemoryEventStore();
        var services = CreateAgentServices(eventStore: store);

        var agent1 = CreateAgent(services);
        await agent1.ActivateAsync();
        await agent1.HandleEnqueueAsync(CreateEnqueueEvent("actor-1", "direct", "cmd-1", ["definition-1"]));
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services);
        await agent2.ActivateAsync();

        agent2.State.Entries.Should().ContainKey("actor-1::cmd-1");
        agent2.State.Entries["actor-1::cmd-1"].ActorId.Should().Be("actor-1");
    }

    [Fact]
    public async Task ReplayHostedService_ShouldTriggerReplayViaOutbox()
    {
        var queryPort = new RecordingQueryPort
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "actor-1",
                WorkflowName = "direct",
                CompletionStatus = WorkflowRunCompletionStatus.Completed,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        var actorPort = new RecordingActorPort();
        var services = CreateAgentServices(queryPort: queryPort, actorPort: actorPort);
        var agent = CreateAgent(services);
        await agent.HandleEnqueueAsync(CreateEnqueueEvent("actor-1", "direct", "cmd-1", ["definition-1"]));

        var outbox = new DirectOutbox(agent);
        var timer = new WorkflowRunDetachedCleanupReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                DetachedCleanupReplayBatchSize = 10,
            });

        await timer.ReplayOnceAsync();

        agent.State.Entries["actor-1::cmd-1"].CompletedAtUtc.Should().NotBeNull();
        actorPort.DestroyCalls.Should().ContainSingle().Which.Should().Be("definition-1");
    }

    private static WorkflowRunDetachedCleanupEnqueuedEvent CreateEnqueueEvent(
        string actorId,
        string workflowName,
        string commandId,
        IReadOnlyList<string> createdActorIds) =>
        new()
        {
            RecordId = WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(actorId, commandId),
            ActorId = actorId,
            WorkflowName = workflowName,
            CommandId = commandId,
            CreatedActorIds = { createdActorIds },
            EnqueuedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed class RecordingQueryPort : IWorkflowExecutionProjectionQueryPort
    {
        public bool EnableActorQueryEndpoints => true;

        public WorkflowActorSnapshot? Snapshot { get; init; }
        public Exception? Exception { get; init; }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            if (Exception != null)
                return Task.FromException<WorkflowActorSnapshot?>(Exception);

            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
            int take = 200,
            CancellationToken ct = default)
        {
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>(Snapshot == null ? [] : [Snapshot]);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = depth;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowActorGraphSubgraph());
        }

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = depth;
            _ = take;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorGraphEnrichedSnapshot?>(null);
        }
    }

    private sealed class RecordingLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public List<WorkflowExecutionProjectionContext> StopCalls { get; } = [];
        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default) => Task.CompletedTask;

        public Task ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            StopCalls.Add(context);
            if (context.StreamSubscriptionLease != null)
                await context.StreamSubscriptionLease.DisposeAsync();

            Stopped.TrySetResult(true);
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingReadModelUpdater : IWorkflowProjectionReadModelUpdater
    {
        public List<string> MarkStoppedActorIds { get; } = [];

        public Task RefreshMetadataAsync(
            string actorId,
            WorkflowExecutionProjectionContext context,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkStoppedAsync(string actorId, CancellationToken ct = default)
        {
            MarkStoppedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOwnershipCoordinator : IProjectionOwnershipCoordinator
    {
        public List<(string ActorId, string CommandId)> AcquireCalls { get; } = [];
        public List<(string ActorId, string CommandId)> ReleaseCalls { get; } = [];

        public Task AcquireAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            AcquireCalls.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }

        public Task<bool> HasActiveLeaseAsync(string scopeId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task ReleaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ReleaseCalls.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorPort : IWorkflowRunActorPort
    {
        public List<string> DestroyCalls { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            return Task.CompletedTask;
        }

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class DirectOutbox(WorkflowRunDetachedCleanupOutboxGAgent agent) : IWorkflowRunDetachedCleanupOutbox
    {
        public Task EnqueueAsync(WorkflowRunDetachedCleanupRequest request, CancellationToken ct = default) =>
            agent.HandleEnqueueAsync(CreateEnqueueEvent(
                request.ActorId,
                request.WorkflowName,
                request.CommandId,
                request.CreatedActorIds));

        public Task DiscardAsync(WorkflowRunDetachedCleanupDiscardRequest request, CancellationToken ct = default) =>
            agent.HandleDiscardAsync(new WorkflowRunDetachedCleanupDiscardedEvent
            {
                RecordId = WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(request.ActorId, request.CommandId),
                DiscardedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            });

        public Task TriggerReplayAsync(int batchSize, CancellationToken ct = default) =>
            agent.HandleTriggerReplayAsync(
                new WorkflowRunDetachedCleanupTriggerReplayEvent
                {
                    BatchSize = batchSize,
                });
    }

    private sealed class RecordingProjectionControlHub : IProjectionSessionEventHub<WorkflowProjectionControlEvent>
    {
        private readonly Dictionary<(string ScopeId, string SessionId), List<Func<WorkflowProjectionControlEvent, ValueTask>>> _handlers = new();

        public TaskCompletionSource<bool> SubscriptionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowProjectionControlEvent evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(evt);

            if (!_handlers.TryGetValue((scopeId, sessionId), out var handlers))
                return;

            foreach (var handler in handlers.ToArray())
                await handler(evt);
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowProjectionControlEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(handler);

            var key = (scopeId, sessionId);
            if (!_handlers.TryGetValue(key, out var handlers))
            {
                handlers = [];
                _handlers[key] = handlers;
            }

            handlers.Add(handler);
            SubscriptionStarted.TrySetResult(true);
            return Task.FromResult<IAsyncDisposable>(new Subscription(_handlers, key, handler));
        }

        private sealed class Subscription(
            Dictionary<(string ScopeId, string SessionId), List<Func<WorkflowProjectionControlEvent, ValueTask>>> handlers,
            (string ScopeId, string SessionId) key,
            Func<WorkflowProjectionControlEvent, ValueTask> handler) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                if (handlers.TryGetValue(key, out var registered))
                {
                    registered.Remove(handler);
                    if (registered.Count == 0)
                        handlers.Remove(key);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingStreamSubscriptionLease(string actorId) : IActorStreamSubscriptionLease
    {
        public string ActorId { get; } = actorId;
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
