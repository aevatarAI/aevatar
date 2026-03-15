using Aevatar.AI.Projection.Reducers;
using Aevatar.AI.Projection.Appliers;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionReportArtifactProjectorTests
{
    private static readonly WorkflowExecutionGraphMaterializer GraphMaterializer = new();

    private static IEventDeduplicator CreateDeduplicator() => new TestEventDeduplicator();
    private static InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: report => report.StartedAt);
    private static IProjectionWriteDispatcher<WorkflowExecutionReport> CreateDispatcher(
        InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> store)
    {
        var graphStore = new InMemoryProjectionGraphStore();
        var bindings = new IProjectionWriteSink<WorkflowExecutionReport>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowExecutionReport>(store),
            new ProjectionGraphStoreBinding<WorkflowExecutionReport>(graphStore, GraphMaterializer),
        };
        return new ProjectionStoreDispatcher<WorkflowExecutionReport>(bindings);
    }

    private static IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> BuildReducers() =>
    [
        new StartWorkflowEventReducer(),
        new StepRequestEventReducer(),
        new StepCompletedEventReducer(),
        new TextMessageStartProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
            [new AITextMessageStartProjectionApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext>()]),
        new TextMessageContentProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
            [new AITextMessageContentProjectionApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext>()]),
        new TextMessageEndProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
            [new AITextMessageEndProjectionApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext>()]),
        new ToolCallProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
            [new AIToolCallProjectionApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext>()]),
        new ToolResultProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
            [new AIToolResultProjectionApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext>()]),
        new WorkflowSuspendedEventReducer(),
        new WorkflowCompletedEventReducer(),
    ];

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
        var store = CreateStore();
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            BuildReducers());
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

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.WorkflowName.Should().Be("direct");
        report.CreatedAt.Should().Be(context.StartedAt);
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
        var store = CreateStore();
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            BuildReducers());
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

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Should().BeEmpty();
        report.Summary.TotalSteps.Should().Be(0);
    }

    [Fact]
    public async Task Projector_ShouldDeduplicateByEnvelopeId()
    {
        var store = CreateStore();
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            BuildReducers());
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

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Count(x => x.Stage == "step.request").Should().Be(1);
        report.StateVersion.Should().Be(1);
        report.LastEventId.Should().Be("evt-dup-1");
    }

    [Fact]
    public async Task Projector_ShouldApplyWorkflowSuspendedEvent()
    {
        var store = CreateStore();
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            BuildReducers());
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
    public async Task Projector_NoOpReducer_ShouldNotAdvanceStateVersion()
    {
        var store = CreateStore();
        IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>[] reducers =
        [
            new TextMessageStartProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>([]),
        ];
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            reducers);
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-noop",
            CommandId = "cmd-noop",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, WrapCommitted(new AIEvents.TextMessageStartEvent
        {
            SessionId = "wf-run-1:s1",
        }, version: 1, id: "evt-noop-1"));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.StateVersion.Should().Be(0);
        report.LastEventId.Should().BeEmpty();
        report.Timeline.Should().BeEmpty();
        report.RoleReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldUseEnvelopeTimestamp_WhenProvided()
    {
        var store = CreateStore();
        var projector = new WorkflowExecutionReportArtifactProjector(
            CreateDispatcher(store),
            store,
            CreateDeduplicator(),
            new SystemProjectionClock(),
            BuildReducers());
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

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
        report.Timeline.Single(x => x.Stage == "workflow.start").Timestamp.UtcDateTime.Should().Be(t);
    }

    [Fact]
    public async Task Store_List_ShouldReturnNewestFirst()
    {
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            Id = "a-older",
            WorkflowName = "w",
            RootActorId = "a-older",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Summary = new WorkflowExecutionSummary(),
        });
        await store.UpsertAsync(new WorkflowExecutionReport
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

        await store.UpsertAsync(new WorkflowExecutionReport
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
}
