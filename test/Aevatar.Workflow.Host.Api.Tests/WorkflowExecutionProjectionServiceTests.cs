using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Projection.Stores;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionProjectionServiceTests
{
    [Fact]
    public async Task EnsureActorProjectionAsync_WhenEnabled_ShouldExposeActorSnapshotAndTimeline()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams);

        await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));
        await streams.GetStream("root").ProduceAsync(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            Success = true,
            Output = "done",
        }));

        await WaitUntilAsync(async () =>
        {
            var timelineItems = await service.ListActorTimelineAsync("root", 50);
            return timelineItems.Any(x => x.Stage == "workflow.start");
        });

        var snapshot = await service.GetActorSnapshotAsync("root");
        var timeline = await service.ListActorTimelineAsync("root", 50);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("root");
        snapshot.LastCommandId.Should().Be("cmd-1");
        timeline.Should().Contain(x => x.Stage == "workflow.start");
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenDisabled_ShouldNoop()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = false,
                EnableActorQueryEndpoints = false,
            },
            out _);

        var sink = new WorkflowRunEventChannel();
        await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        await service.AttachLiveSinkAsync("root", sink);
        await service.DetachLiveSinkAsync("root", sink);

        var snapshot = await service.GetActorSnapshotAsync("root");
        var timeline = await service.ListActorTimelineAsync("root", 50);
        snapshot.Should().BeNull();
        timeline.Should().BeEmpty();
    }

    private static WorkflowExecutionProjectionService CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams)
    {
        streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        var store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(store, BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);
        var dispatcher = new ProjectionDispatcher<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator);
        var runRegistry = new ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>(
            dispatcher,
            subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            coordinator,
            dispatcher,
            runRegistry);
        return new WorkflowExecutionProjectionService(
            options,
            lifecycle,
            store,
            new SystemProjectionClock(),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            new WorkflowExecutionReadModelMapper());
    }

    private static IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> BuildReducers() =>
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
