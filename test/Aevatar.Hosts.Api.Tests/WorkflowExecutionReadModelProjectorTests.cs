using Aevatar.CQRS.Projections.Abstractions;
using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.CQRS.Projections.Orchestration;
using Aevatar.CQRS.Projections.Projectors;
using Aevatar.CQRS.Projections.Reducers;
using Aevatar.CQRS.Projections.Stores;
using Aevatar.Workflows.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Hosts.Api.Tests;

public class WorkflowExecutionReadModelProjectorTests
{
    private static IReadOnlyList<IWorkflowExecutionEventReducer> BuildReducers() =>
    [
        new StartWorkflowEventReducer(),
        new StepRequestEventReducer(),
        new StepCompletedEventReducer(),
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
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);

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
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);

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
    public async Task Projector_ShouldDeduplicateByEnvelopeId()
    {
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);

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
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);

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
