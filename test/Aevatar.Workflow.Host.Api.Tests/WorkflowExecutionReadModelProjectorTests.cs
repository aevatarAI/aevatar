using Aevatar.AI.Projection.Reducers;
using Aevatar.AI.Projection.Appliers;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
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

public class WorkflowExecutionReadModelProjectorTests
{
    private static IEventDeduplicator CreateDeduplicator() => new TestEventDeduplicator();
    private static InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        listSortSelector: report => report.StartedAt);
    private static IProjectionStoreDispatcher<WorkflowExecutionReport, string> CreateDispatcher(
        InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> store)
    {
        var graphStore = new InMemoryProjectionGraphStore();
        var bindings = new IProjectionStoreBinding<WorkflowExecutionReport, string>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowExecutionReport, string>(store),
            new ProjectionGraphStoreBinding<WorkflowExecutionReport, string>(graphStore),
        };
        return new ProjectionStoreDispatcher<WorkflowExecutionReport, string>(bindings);
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

    private static EventEnvelope Wrap(
        IMessage evt,
        string publisherId = "root",
        string? id = null,
        DateTime? utcTimestamp = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime()),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Down,
    };

    [Fact]
    public async Task Projector_ShouldBuildRunReadModel_EndToEnd()
    {
        var store = CreateStore();
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        await coordinator.ProjectAsync(context, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));
        await coordinator.ProjectAsync(context, Wrap(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }));
        await coordinator.ProjectAsync(context, Wrap(new StepCompletedEvent
        {
            StepId = "s1",
            Success = true,
            Output = "done",
            WorkerId = "assistant",
        }));
        await coordinator.ProjectAsync(context, Wrap(new AIEvents.TextMessageEndEvent
        {
            SessionId = "wf-run-1:s1",
            Content = "analysis result",
        }, "assistant"));
        await coordinator.ProjectAsync(context, Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            Success = true,
            Output = "final answer",
        }));
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
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent
        {
            Prompt = "hello",
        }));
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
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        var evt = Wrap(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }, id: "evt-dup-1");

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
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        await coordinator.ProjectAsync(context, Wrap(new WorkflowSuspendedEvent
        {
            StepId = "s1",
            SuspensionType = "human_input",
            Prompt = "Need approval",
            TimeoutSeconds = 60,
            ResumeToken = "resume-token-1",
        }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync("root");
        report.Should().NotBeNull();
        var step = report!.Steps.Should().ContainSingle(x => x.StepId == "s1").Subject;
        step.CompletionMetadata.Should().ContainKey("suspension_type").WhoseValue.Should().Be("human_input");
        step.CompletionMetadata.Should().ContainKey("suspension_prompt").WhoseValue.Should().Be("Need approval");
        step.CompletionMetadata.Should().ContainKey("suspension_timeout").WhoseValue.Should().Be("60");
        step.CompletionMetadata.Should().ContainKey("resume_token").WhoseValue.Should().Be("resume-token-1");
        var suspendedEvent = report.Timeline.Should().ContainSingle(x => x.Stage == "workflow.suspended").Subject;
        suspendedEvent.Data.Should().ContainKey("suspension_type").WhoseValue.Should().Be("human_input");
        suspendedEvent.Data.Should().ContainKey("prompt").WhoseValue.Should().Be("Need approval");
        suspendedEvent.Data.Should().ContainKey("timeout_seconds").WhoseValue.Should().Be("60");
        suspendedEvent.Data.Should().ContainKey("resume_token").WhoseValue.Should().Be("resume-token-1");
    }

    [Fact]
    public async Task Projector_NoOpReducer_ShouldNotAdvanceStateVersion()
    {
        var store = CreateStore();
        IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>[] reducers =
        [
            new TextMessageStartProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>([]),
        ];
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        await coordinator.ProjectAsync(context, Wrap(new AIEvents.TextMessageStartEvent
        {
            SessionId = "wf-run-1:s1",
        }, id: "evt-noop-1"));
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
        var projector = new WorkflowExecutionReadModelProjector(
            CreateDispatcher(store),
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
        await coordinator.ProjectAsync(context, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, utcTimestamp: t));
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
            WorkflowName = "w",
            RootActorId = "a-older",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Summary = new WorkflowExecutionSummary(),
        });
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            WorkflowName = "w",
            RootActorId = "a-newer",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            Summary = new WorkflowExecutionSummary(),
        });

        var runs = await store.ListAsync(10);
        runs.Should().HaveCount(2);
        runs[0].RootActorId.Should().Be("a-newer");
        runs[1].RootActorId.Should().Be("a-older");
    }

    [Fact]
    public async Task Store_MutateMissingRun_ShouldThrow()
    {
        var store = CreateStore();
        Func<Task> act = () => store.MutateAsync("missing", _ => { });
        await act.Should().ThrowAsync<InvalidOperationException>();
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
