using Aevatar.Cqrs.Projections.Abstractions;
using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Cqrs.Projections.Configuration;
using Aevatar.Cqrs.Projections.Orchestration;
using Aevatar.Cqrs.Projections.Projectors;
using Aevatar.Cqrs.Projections.Reducers;
using Aevatar.Cqrs.Projections.Stores;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflows.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Hosts.Api.Tests;

public class ChatRunProjectionServiceTests
{
    [Fact]
    public async Task StartComplete_WhenEnabled_ShouldProjectFromStreamSubscription()
    {
        var options = new ChatProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryChatRunReadModelStore();
        var projector = new ChatRunReadModelProjector(store, BuildReducers());
        var coordinator = new ChatProjectionCoordinator([projector]);
        var service = new ChatRunProjectionService(options, coordinator, store, streams);

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
        var options = new ChatProjectionOptions
        {
            Enabled = false,
            EnableRunQueryEndpoints = false,
            EnableRunReportArtifacts = false,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryChatRunReadModelStore();
        var projector = new ChatRunReadModelProjector(store, BuildReducers());
        var coordinator = new ChatProjectionCoordinator([projector]);
        var service = new ChatRunProjectionService(options, coordinator, store, streams);

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
        var options = new ChatProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryChatRunReadModelStore();
        var projector = new ChatRunReadModelProjector(store, BuildReducers());
        var coordinator = new ChatProjectionCoordinator([projector]);
        var service = new ChatRunProjectionService(options, coordinator, store, streams);

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
        var options = new ChatProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryChatRunReadModelStore();
        var projector = new ChatRunReadModelProjector(store, BuildReducers());
        var coordinator = new ChatProjectionCoordinator([projector]);
        var service = new ChatRunProjectionService(options, coordinator, store, streams);

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
        var options = new ChatProjectionOptions
        {
            Enabled = true,
            EnableRunQueryEndpoints = true,
            EnableRunReportArtifacts = true,
            RunProjectionCompletionWaitTimeoutMs = 50,
        };
        var streams = new InMemoryStreamProvider();
        var store = new InMemoryChatRunReadModelStore();
        var projector = new ChatRunReadModelProjector(store, BuildReducers());
        var coordinator = new ChatProjectionCoordinator([projector]);
        var service = new ChatRunProjectionService(options, coordinator, store, streams);

        var session = await service.StartAsync("root", "direct", "hello");
        var completed = await service.WaitForRunProjectionCompletedAsync(session.RunId);

        completed.Should().BeFalse();
        _ = await service.CompleteAsync(session, []);
    }

    private static IReadOnlyList<IChatRunEventReducer> BuildReducers() =>
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
}
