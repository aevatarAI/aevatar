using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Projection.Stores;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Host.Api.Tests;

public class WorkflowExecutionReadModelProjectorTests
{
    private static IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> BuildReducers() =>
    [
        new StartWorkflowEventReducer(),
        new StepRequestEventReducer(),
        new StepCompletedEventReducer(),
        new WorkflowSuspendedEventReducer(),
        new TextMessageEndEventReducer(),
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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "run-1",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
            Input = "hello",
        }));
        await coordinator.ProjectAsync(context, Wrap(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            RunId = "wf-run-1",
            TargetRole = "assistant",
        }));
        await coordinator.ProjectAsync(context, Wrap(new StepCompletedEvent
        {
            StepId = "s1",
            RunId = "wf-run-1",
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
            RunId = "wf-run-1",
            Success = true,
            Output = "final answer",
        }));
        await coordinator.CompleteAsync(context, [new WorkflowExecutionTopologyEdge("root", "assistant")]);

        var report = await store.GetAsync("run-1");
        report.Should().NotBeNull();
        report!.WorkflowName.Should().Be("direct");
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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "run-unknown",
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

        var report = await store.GetAsync("run-unknown");
        report.Should().NotBeNull();
        report!.Timeline.Should().BeEmpty();
        report.Summary.TotalSteps.Should().Be(0);
    }

    [Fact]
    public async Task Projector_ShouldProjectWorkflowSuspendedEvent_ToTimelineAndStepMetadata()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "run-suspend",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-suspend",
            Input = "hello",
        }));
        await coordinator.ProjectAsync(context, Wrap(new StepRequestEvent
        {
            StepId = "get_context",
            StepType = "human_input",
            RunId = "wf-run-suspend",
            TargetRole = "",
            Input = "hello",
        }));
        await coordinator.ProjectAsync(context, Wrap(new WorkflowSuspendedEvent
        {
            RunId = "wf-run-suspend",
            StepId = "get_context",
            SuspensionType = "human_input",
            Prompt = "请提供补充信息",
            TimeoutSeconds = 1800,
            Metadata = { { "variable", "user_context" } },
        }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync("run-suspend");
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.suspended");

        var step = report.Steps.Should().ContainSingle(x => x.StepId == "get_context").Subject;
        step.CompletionMetadata.Should().ContainKey("suspension_type").WhoseValue.Should().Be("human_input");
        step.CompletionMetadata.Should().ContainKey("suspension_prompt").WhoseValue.Should().Be("请提供补充信息");
        step.CompletionMetadata.Should().ContainKey("suspension_timeout").WhoseValue.Should().Be("1800");
    }

    [Fact]
    public async Task Projector_ShouldDeduplicateByEnvelopeId()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "run-dedup",
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
            RunId = "wf-run-1",
            TargetRole = "assistant",
        }, id: "evt-dup-1");

        await coordinator.ProjectAsync(context, evt);
        await coordinator.ProjectAsync(context, evt);
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync("run-dedup");
        report.Should().NotBeNull();
        report!.Timeline.Count(x => x.Stage == "step.request").Should().Be(1);
    }

    [Fact]
    public async Task Projector_ShouldUseEnvelopeTimestamp_WhenProvided()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "run-ts",
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
            RunId = "wf-run-1",
            Input = "hello",
        }, utcTimestamp: t));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync("run-ts");
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
        report.Timeline.Single(x => x.Stage == "workflow.start").Timestamp.UtcDateTime.Should().Be(t);
    }

    [Fact]
    public async Task Store_List_ShouldReturnNewestFirst()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RunId = "older",
            WorkflowName = "w",
            RootActorId = "a",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Summary = new WorkflowExecutionSummary(),
        });
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RunId = "newer",
            WorkflowName = "w",
            RootActorId = "a",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            Summary = new WorkflowExecutionSummary(),
        });

        var runs = await store.ListAsync(10);
        runs.Should().HaveCount(2);
        runs[0].RunId.Should().Be("newer");
        runs[1].RunId.Should().Be("older");
    }

    [Fact]
    public async Task Store_MutateMissingRun_ShouldThrow()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        Func<Task> act = () => store.MutateAsync("missing", _ => { });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
