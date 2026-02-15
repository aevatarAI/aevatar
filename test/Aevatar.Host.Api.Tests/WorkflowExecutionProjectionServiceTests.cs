using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Projection.Stores;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Diagnostics;

namespace Aevatar.Host.Api.Tests;

public class WorkflowExecutionProjectionServiceTests
{
    [Fact]
    public async Task StartComplete_WhenEnabled_ShouldProjectFromStreamSubscription()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Input = "hello",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Success = true,
            Output = "done",
        }));

        await WaitUntilAsync(async () =>
        {
            var report = await store.GetAsync(session.RunId);
            return report?.Timeline.Any(x => x.Stage == "workflow.start") == true;
        });
        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);

        var report = await service.CompleteAsync(session, []);

        completed.Should().BeTrue();
        report.Should().NotBeNull();
        report!.RunId.Should().Be(session.RunId);
        report.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
    }

    [Fact]
    public async Task StartProjectComplete_WhenDisabled_ShouldNoop()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = false,
            EnableRunQueryEndpoints = false,
            EnableRunReportArtifacts = false,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        await service.ProjectAsync(session, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
            Input = "hello",
        }));
        var report = await service.CompleteAsync(session, []);
        var runs = await service.ListRunsAsync();
        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);

        session.Enabled.Should().BeFalse();
        report.Should().BeNull();
        runs.Should().BeEmpty();
        completed.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectAsync_ShouldRemainAvailable_ForManualProjection()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        await service.ProjectAsync(session, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Input = "hello",
        }));
        var report = await service.CompleteAsync(session, []);

        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
    }

    [Fact]
    public async Task CompleteAsync_WithMultipleRunsOnSameActor_ShouldShareActorProjectionStream()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session1 = await service.StartAsync("root", "direct", "hello-1");
        var session2 = await service.StartAsync("root", "direct", "hello-2");

        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session1.RunId,
            Input = "hello-1",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session2.RunId,
            Input = "hello-2",
        }));

        await WaitUntilAsync(async () =>
        {
            var report1 = await store.GetAsync(session1.RunId);
            var report2 = await store.GetAsync(session2.RunId);
            return report1?.Timeline.Any(x => x.Stage == "workflow.start") == true
                && report2?.Timeline.Any(x => x.Stage == "workflow.start") == true;
        });

        var report1BeforeComplete = await store.GetAsync(session1.RunId);
        var report2BeforeComplete = await store.GetAsync(session2.RunId);
        report1BeforeComplete.Should().NotBeNull();
        report2BeforeComplete.Should().NotBeNull();
        report1BeforeComplete!.Timeline.Count(x => x.Stage == "workflow.start").Should().Be(2);
        report2BeforeComplete!.Timeline.Count(x => x.Stage == "workflow.start").Should().Be(2);

        _ = await service.CompleteAsync(session1, []);

        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session2.RunId,
            Input = "hello-again",
        }));

        await WaitUntilAsync(async () =>
        {
            var report2 = await store.GetAsync(session2.RunId);
            return report2?.Timeline.Count(x => x.Stage == "workflow.start") >= 2;
        });

        var secondReport = await service.CompleteAsync(session2, []);
        secondReport.Should().NotBeNull();
        secondReport!.Timeline.Count(x => x.Stage == "workflow.start").Should().Be(3);
    }

    [Fact]
    public async Task WaitForRunProjectionCompletedAsync_WhenNoTerminalEvent_ShouldTimeoutFalse()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
            RunProjectionCompletionWaitTimeoutMs = 50,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);

        completed.Should().BeFalse();
        _ = await service.CompleteAsync(session, []);
    }

    [Fact]
    public async Task WaitForRunProjectionCompletedAsync_WhenProjectionFails_ShouldReturnFalseWithoutWaitingTimeout()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
            RunProjectionCompletionWaitTimeoutMs = 3000,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [new FailingProjector()]);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Input = "hello",
        }));

        var sw = Stopwatch.StartNew();
        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);
        sw.Stop();

        completed.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
        _ = await service.CompleteAsync(session, []);
    }

    [Fact]
    public async Task ProjectionRun_ShouldIgnoreEventsAfterTerminalEnvelope_BeforeSessionComplete()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>( [projector]);
        var runRegistry = CreateRegistry(coordinator, subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator, runRegistry);
        var service = CreateService(options, lifecycle, store);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Input = "hello",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            RunId = session.RunId,
            Success = true,
            Output = "done",
        }));

        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);
        completed.Should().BeTrue();

        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-2",
            Input = "late-event",
        }));

        await Task.Delay(50);
        var reportBeforeComplete = await store.GetAsync(session.RunId);
        reportBeforeComplete.Should().NotBeNull();
        reportBeforeComplete!.Timeline.Count(x => x.Stage == "workflow.start").Should().Be(1);

        _ = await service.CompleteAsync(session, []);
    }

    private static IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> BuildReducers() =>
    [
        new StartWorkflowEventReducer(),
        new StepRequestEventReducer(),
        new StepCompletedEventReducer(),
        new TextMessageEndEventReducer(),
        new WorkflowCompletedEventReducer(),
    ];

    private static ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> CreateRegistry(
        ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> coordinator,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub) =>
        new(
            coordinator,
            subscriptionHub,
            new WorkflowCompletedEventProjectionCompletionDetector<WorkflowExecutionProjectionContext>());

    private static WorkflowExecutionProjectionService CreateService(
        WorkflowExecutionProjectionOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store) =>
        new(
            options,
            lifecycle,
            store,
            new GuidProjectionRunIdGenerator(),
            new SystemProjectionClock(),
            new DefaultWorkflowExecutionProjectionContextFactory());

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "root") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Down,
    };

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        int timeoutMs = 1000,
        int pollMs = 10)
    {
        var started = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - started).TotalMilliseconds < timeoutMs)
        {
            if (await predicate())
                return;

            await Task.Delay(pollMs);
        }

        throw new TimeoutException("Condition not met before timeout.");
    }

    private sealed class FailingProjector : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public int Order => 0;

        public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            ValueTask.FromException(new InvalidOperationException("projection failed"));

        public ValueTask CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
