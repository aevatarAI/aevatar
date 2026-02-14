using Aevatar.CQRS.Projections.Abstractions;
using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.CQRS.Projections.Configuration;
using Aevatar.CQRS.Projections.Orchestration;
using Aevatar.CQRS.Projections.Projectors;
using Aevatar.CQRS.Projections.Reducers;
using Aevatar.CQRS.Projections.Stores;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflows.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Diagnostics;

namespace Aevatar.Hosts.Api.Tests;

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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
            Input = "hello",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

        var session = await service.StartAsync("root", "direct", "hello");
        await service.ProjectAsync(session, Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-2",
            Input = "hello",
        }));
        var report = await service.CompleteAsync(session, []);

        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "workflow.start");
    }

    [Fact]
    public async Task CompleteAsync_WithMultipleRunsOnSameActor_ShouldKeepSubscriptionForRemainingRun()
    {
        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

        var session1 = await service.StartAsync("root", "direct", "hello-1");
        var session2 = await service.StartAsync("root", "direct", "hello-2");

        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-a",
            Input = "hello",
        }));

        await WaitUntilAsync(async () =>
        {
            var report1 = await store.GetAsync(session1.RunId);
            var report2 = await store.GetAsync(session2.RunId);
            return report1?.Timeline.Any(x => x.Stage == "workflow.start") == true
                && report2?.Timeline.Any(x => x.Stage == "workflow.start") == true;
        });

        _ = await service.CompleteAsync(session1, []);

        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-b",
            Input = "hello-again",
        }));

        await WaitUntilAsync(async () =>
        {
            var report2 = await store.GetAsync(session2.RunId);
            return report2?.Timeline.Count(x => x.Stage == "workflow.start") >= 2;
        });

        var secondReport = await service.CompleteAsync(session2, []);
        secondReport.Should().NotBeNull();
        secondReport!.Timeline.Count(x => x.Stage == "workflow.start").Should().BeGreaterThanOrEqualTo(2);
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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

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
        var coordinator = new WorkflowExecutionProjectionCoordinator([new FailingProjector()]);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-fail",
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
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new WorkflowExecutionProjectionCoordinator([projector]);
        var runRegistry = new WorkflowExecutionProjectionSubscriptionRegistry(coordinator, streams);
        var service = new WorkflowExecutionProjectionService(options, coordinator, store, runRegistry);

        var session = await service.StartAsync("root", "direct", "hello");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
            Input = "hello",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            RunId = "wf-run-1",
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

    private static IReadOnlyList<IWorkflowExecutionEventReducer> BuildReducers() =>
    [
        new StartWorkflowEventReducer(),
        new StepRequestEventReducer(),
        new StepCompletedEventReducer(),
        new TextMessageEndEventReducer(),
        new WorkflowCompletedEventReducer(),
    ];

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
