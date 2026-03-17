using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using AIEvents = Aevatar.AI.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowRunInsightBridgeProjectorTests
{
    private static IEventDeduplicator CreateDeduplicator() => new TestEventDeduplicator();
    private static RecordingProjectionDocumentStore CreateStore() => new();

    private static IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> CreateDispatcher(
        RecordingProjectionDocumentStore store)
    {
        var bindings = new IProjectionWriteSink<WorkflowRunInsightReportDocument>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowRunInsightReportDocument>(store),
        };
        return new ProjectionStoreDispatcher<WorkflowRunInsightReportDocument>(bindings);
    }

    private static ProjectorHarness CreateHarness(IProjectionClock? clock = null)
    {
        var store = CreateStore();
        var dispatcher = CreateDispatcher(store);
        var resolvedClock = clock ?? new SystemProjectionClock();

        var forwardingRegistry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            forwardingRegistry);
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(
            new TestActorEventSubscriptionProvider(streams));

        var runtimeServices = new ServiceCollection();
        runtimeServices.AddSingleton<IStreamProvider>(streams);
        runtimeServices.AddSingleton<IEventStore, InMemoryEventStore>();
        runtimeServices.AddSingleton<EventSourcingRuntimeOptions>();
        runtimeServices.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        runtimeServices.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        runtimeServices.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        var runtimeProvider = runtimeServices.BuildServiceProvider();

        var runtime = new LocalActorRuntime(streams, runtimeProvider, streams);
        var dispatchPort = new LocalActorDispatchPort(runtime);
        var agentTypeVerifier = new DefaultAgentTypeVerifier(new RuntimeActorTypeProbe(runtime));
        var insightActorPort = new ActorWorkflowRunInsightPort(runtime, dispatchPort, agentTypeVerifier);

        var insightProjector = new WorkflowRunInsightReportDocumentProjector(dispatcher);
        var insightCoordinator = new ProjectionCoordinator<WorkflowRunInsightProjectionContext, bool>([insightProjector]);
        var insightDispatcher = new ProjectionDispatcher<WorkflowRunInsightProjectionContext, bool>(insightCoordinator);
        var insightRegistry = new ProjectionSubscriptionRegistry<WorkflowRunInsightProjectionContext>(
            insightDispatcher,
            subscriptionHub);
        var insightLifecycle = new ProjectionLifecycleService<WorkflowRunInsightProjectionContext, bool>(
            insightCoordinator,
            insightDispatcher,
            insightRegistry);
        var insightActivation = new ContextProjectionActivationService<WorkflowRunInsightRuntimeLease, WorkflowRunInsightProjectionContext, bool>(
            insightLifecycle,
            (rootActorId, _, _, _, _) => new WorkflowRunInsightProjectionContext
            {
                ProjectionId = $"{rootActorId}:insight",
                RootActorId = WorkflowRunInsightGAgent.BuildActorId(rootActorId),
                RunActorId = rootActorId,
            },
            context => new WorkflowRunInsightRuntimeLease(context));

        return new ProjectorHarness(
            Store: store,
            Projector: new WorkflowRunInsightBridgeProjector(
                insightActorPort,
                insightActivation,
                CreateDeduplicator(),
                resolvedClock));
    }

    private static EventEnvelope WrapCommitted(
        IMessage evt,
        long version,
        string publisherId = "root",
        string? id = null,
        DateTime? utcTimestamp = null)
    {
        var eventId = id ?? Guid.NewGuid().ToString("N");
        var occurredAt = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime());
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new WorkflowRunState()),
            }),
        };
    }

    [Fact]
    public async Task Projector_ShouldBuildRunReadModel_EndToEnd()
    {
        var harness = CreateHarness();
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-1",
            CommandId = "cmd-1",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, version: 1));
        await coordinator.ProjectAsync(context, WrapCommitted(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }, version: 2));
        await coordinator.ProjectAsync(context, WrapCommitted(new StepCompletedEvent
        {
            StepId = "s1",
            Success = true,
            Output = "done",
            WorkerId = "assistant",
        }, version: 3));
        await coordinator.ProjectAsync(context, WrapCommitted(new AIEvents.TextMessageEndEvent
        {
            SessionId = "wf-run-1:s1",
            Content = "analysis result",
        }, version: 4, publisherId: "assistant"));
        await coordinator.ProjectAsync(context, WrapCommitted(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            Success = true,
            Output = "final answer",
        }, version: 5));
        await coordinator.CompleteAsync(context, [new WorkflowExecutionTopologyEdge("root", "assistant")]);

        await store.WaitForAsync(
            "root",
            report => report is not null &&
                      report.Topology.Any(x => x.Parent == "root" && x.Child == "assistant") &&
                      report.Timeline.Any(x => x.Stage == "workflow.completed"),
            TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.WorkflowName.Should().Be("direct");
        report.CreatedAt.Should().Be(report.StartedAt);
        report.UpdatedAt.Should().BeOnOrAfter(report.CreatedAt);
        report.Success.Should().BeTrue();
        report.FinalOutput.Should().Be("final answer");
        report.Summary.TotalSteps.Should().Be(1);
        report.Summary.CompletedSteps.Should().Be(1);
        report.Summary.StepTypeCounts.Should().ContainKey("llm_call").WhoseValue.Should().Be(1);
        report.RoleReplies.Should().ContainSingle(x => x.RoleId == "assistant");
        report.Topology.Should().ContainSingle(x => x.Parent == "root" && x.Child == "assistant");
        report.Timeline.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldIgnoreUnknownEvents()
    {
        var harness = CreateHarness();
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-unknown",
            CommandId = "cmd-unknown",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new ChatRequestEvent
        {
            Prompt = "hello",
        }, version: 1));
        await coordinator.CompleteAsync(context, []);

        await store.WaitForReportAsync("root", TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Should().BeEmpty();
        report.Summary.TotalSteps.Should().Be(0);
    }

    [Fact]
    public async Task Projector_ShouldDeduplicateByEnvelopeId()
    {
        var harness = CreateHarness();
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-dedup",
            CommandId = "cmd-dedup",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        var evt = WrapCommitted(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }, version: 1, id: "evt-dup-1");

        await coordinator.ProjectAsync(context, evt);
        await coordinator.ProjectAsync(context, evt);
        await coordinator.CompleteAsync(context, []);

        await store.WaitForAsync(
            "root",
            report => report?.StateVersion >= 2 &&
                      report.Timeline.Any(x => x.Stage == "step.request"),
            TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Count(x => x.Stage == "step.request").Should().Be(1);
        report.StateVersion.Should().Be(2);
        report.LastEventId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Projector_ShouldApplyWorkflowSuspendedEvent()
    {
        var harness = CreateHarness();
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-suspended",
            CommandId = "cmd-suspended",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new WorkflowSuspendedEvent
        {
            StepId = "s1",
            SuspensionType = "human_input",
            Prompt = "Need approval",
            TimeoutSeconds = 60,
        }, version: 1));
        await coordinator.CompleteAsync(context, []);

        await store.WaitForTimelineStageAsync("root", "workflow.suspended", TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        var step = report!.Steps.Should().ContainSingle(x => x.StepId == "s1").Subject;
        step.SuspensionType.Should().Be("human_input");
        step.SuspensionPrompt.Should().Be("Need approval");
        step.SuspensionTimeoutSeconds.Should().Be(60);
        var suspendedEvent = report.Timeline.Should().ContainSingle(x => x.Stage == "workflow.suspended").Subject;
        suspendedEvent.Data.Should().ContainKey("suspension_type").WhoseValue.Should().Be("human_input");
        suspendedEvent.Data.Should().ContainKey("prompt").WhoseValue.Should().Be("Need approval");
        suspendedEvent.Data.Should().ContainKey("timeout_seconds").WhoseValue.Should().Be("60");
    }

    [Fact]
    public async Task Projector_ShouldApplyWorkflowStoppedEvent()
    {
        var harness = CreateHarness();
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-stopped",
            CommandId = "cmd-stopped",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new WorkflowStoppedEvent
        {
            WorkflowName = "direct",
            RunId = "run-1",
            Reason = "user requested stop",
        }, version: 1));
        await coordinator.CompleteAsync(context, []);

        await store.WaitForTimelineStageAsync("root", "workflow.stopped", TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.FinalError.Should().Be("user requested stop");
        report.Timeline.Should().ContainSingle(x => x.Stage == "workflow.stopped");
    }

    [Fact]
    public async Task Projector_ShouldUseEnvelopeTimestamp_WhenProvided()
    {
        var harness = CreateHarness(new SystemProjectionClock());
        var store = harness.Store;
        var projector = harness.Projector;
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-ts",
            CommandId = "cmd-ts",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        var t = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, version: 1, utcTimestamp: t));
        await coordinator.CompleteAsync(context, []);

        await store.WaitForTimelineStageAsync("root", "workflow.start", TimeSpan.FromSeconds(5));
        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
        report.Timeline.Single(x => x.Stage == "workflow.start").Timestamp.UtcDateTime.Should().Be(t);
    }

    [Fact]
    public async Task Store_List_ShouldReturnNewestFirst()
    {
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowRunInsightReportDocument
        {
            Id = "a-older",
            WorkflowName = "w",
            RootActorId = "a-older",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Summary = new WorkflowExecutionSummary(),
        });
        await store.UpsertAsync(new WorkflowRunInsightReportDocument
        {
            Id = "a-newer",
            WorkflowName = "w",
            RootActorId = "a-newer",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            Summary = new WorkflowExecutionSummary(),
        });

        var runs = await store.QueryAsync(new ProjectionDocumentQuery
        {
            Take = 10,
        });
        runs.Items.Should().HaveCount(2);
        runs.Items[0].RootActorId.Should().Be("a-newer");
        runs.Items[1].RootActorId.Should().Be("a-older");
    }

    [Fact]
    public async Task Store_GetMissingRun_ShouldReturnNull_AndAllowUpsert()
    {
        var store = CreateStore();
        var missing = await store.GetAsync("missing");
        missing.Should().BeNull();

        await store.UpsertAsync(new WorkflowRunInsightReportDocument
        {
            Id = "missing",
            RootActorId = "missing",
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Summary = new WorkflowExecutionSummary(),
        });

        var persisted = await store.GetAsync("missing");
        persisted.Should().NotBeNull();
        persisted!.RootActorId.Should().Be("missing");
    }

    private sealed class TestEventDeduplicator : IEventDeduplicator
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Task<bool> TryRecordAsync(string eventId)
        {
            lock (_gate)
                return Task.FromResult(_seen.Add(eventId));
        }
    }

    private sealed record ProjectorHarness(
        RecordingProjectionDocumentStore Store,
        WorkflowRunInsightBridgeProjector Projector);

    private sealed class RecordingProjectionDocumentStore
        : IProjectionDocumentWriter<WorkflowRunInsightReportDocument>,
          IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>
    {
        private readonly InMemoryProjectionDocumentStore<WorkflowRunInsightReportDocument, string> _inner = new(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: report => report.StartedAt);
        private readonly object _gate = new();
        private readonly List<StoreWaiter> _waiters = [];

        public async Task<ProjectionWriteResult> UpsertAsync(WorkflowRunInsightReportDocument readModel, CancellationToken ct = default)
        {
            var result = await _inner.UpsertAsync(readModel, ct);
            if (result.IsApplied)
                await NotifyWaitersAsync(readModel.RootActorId, ct);

            return result;
        }

        public Task<WorkflowRunInsightReportDocument?> GetAsync(string key, CancellationToken ct = default) =>
            _inner.GetAsync(key, ct);

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);

        public async Task WaitForReportAsync(string actorId, TimeSpan timeout)
        {
            if (await GetAsync(actorId) != null)
                return;

            var waiter = new StoreWaiter(actorId, report => report != null);
            Register(waiter);

            try
            {
                if (await GetAsync(actorId) != null)
                    waiter.Signal.TrySetResult(true);

                await waiter.Signal.Task.WaitAsync(timeout);
            }
            finally
            {
                Unregister(waiter);
            }
        }

        public async Task WaitForTimelineStageAsync(string actorId, string stage, TimeSpan timeout)
        {
            if (await HasTimelineStageAsync(actorId, stage))
                return;

            var waiter = new StoreWaiter(
                actorId,
                report => report?.Timeline.Any(x => string.Equals(x.Stage, stage, StringComparison.Ordinal)) == true);
            Register(waiter);

            try
            {
                if (await HasTimelineStageAsync(actorId, stage))
                    waiter.Signal.TrySetResult(true);

                await waiter.Signal.Task.WaitAsync(timeout);
            }
            finally
            {
                Unregister(waiter);
            }
        }

        public async Task WaitForAsync(
            string actorId,
            Func<WorkflowRunInsightReportDocument?, bool> predicate,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(predicate);

            if (predicate(await GetAsync(actorId)))
                return;

            var waiter = new StoreWaiter(actorId, predicate);
            Register(waiter);

            try
            {
                if (predicate(await GetAsync(actorId)))
                    waiter.Signal.TrySetResult(true);

                await waiter.Signal.Task.WaitAsync(timeout);
            }
            finally
            {
                Unregister(waiter);
            }
        }

        private async Task<bool> HasTimelineStageAsync(string actorId, string stage)
        {
            var report = await _inner.GetAsync(actorId);
            return report?.Timeline.Any(x => string.Equals(x.Stage, stage, StringComparison.Ordinal)) == true;
        }

        private void Register(StoreWaiter waiter)
        {
            lock (_gate)
                _waiters.Add(waiter);
        }

        private void Unregister(StoreWaiter waiter)
        {
            lock (_gate)
                _waiters.Remove(waiter);
        }

        private async Task NotifyWaitersAsync(string actorId, CancellationToken ct)
        {
            List<StoreWaiter> snapshot;
            lock (_gate)
                snapshot = _waiters.Where(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal)).ToList();

            var report = await _inner.GetAsync(actorId, ct);
            foreach (var waiter in snapshot)
            {
                if (waiter.Predicate(report))
                    waiter.Signal.TrySetResult(true);
            }
        }

        private sealed record StoreWaiter(
            string ActorId,
            Func<WorkflowRunInsightReportDocument?, bool> Predicate)
        {
            public TaskCompletionSource<bool> Signal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class RuntimeActorTypeProbe(IActorRuntime runtime) : IActorTypeProbe
    {
        public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            var actor = await runtime.GetAsync(actorId);
            var runtimeType = actor?.Agent.GetType();
            return runtimeType?.AssemblyQualifiedName ?? runtimeType?.FullName;
        }
    }
}
