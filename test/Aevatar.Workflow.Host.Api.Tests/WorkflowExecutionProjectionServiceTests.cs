using Aevatar.AI.Projection.Reducers;
using Aevatar.AI.Projection.Appliers;
using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
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
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

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
            out var streams,
            out _);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
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
            out _,
            out _);

        var sink = new WorkflowRunEventChannel();
        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().BeNull();

        var snapshot = await service.GetActorSnapshotAsync("root");
        var timeline = await service.ListActorTimelineAsync("root", 50);
        snapshot.Should().BeNull();
        timeline.Should().BeEmpty();
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenCalledRepeatedly_ShouldThrow()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _);

        var firstLease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        firstLease.Should().NotBeNull();

        var act = async () =>
            await service.EnsureActorProjectionAsync("root", "direct", "hello again", "cmd-2");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenStartedConcurrently_ShouldAllowOnlyOneLease()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _);

        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IWorkflowExecutionProjectionLease?> StartAsync(string commandId) => Task.Run(async () =>
        {
            await startGate.Task;
            return await service.EnsureActorProjectionAsync("root", "direct", "hello", commandId);
        });

        var firstTask = StartAsync("cmd-1");
        var secondTask = StartAsync("cmd-2");
        startGate.SetResult(true);

        var firstOutcome = await CaptureLeaseOutcomeAsync(firstTask);
        var secondOutcome = await CaptureLeaseOutcomeAsync(secondTask);

        var successfulLeases = new[] { firstOutcome.Lease, secondOutcome.Lease }
            .Where(x => x != null)
            .Cast<IWorkflowExecutionProjectionLease>()
            .ToList();
        var errors = new[] { firstOutcome.Error, secondOutcome.Error }
            .Where(x => x != null)
            .ToList();

        successfulLeases.Should().HaveCount(1);
        errors.Should().HaveCount(1);
        errors[0].Should().BeOfType<InvalidOperationException>();

        await service.ReleaseActorProjectionAsync(successfulLeases[0]);
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_AfterRelease_ShouldAllowRestart()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _);

        var firstLease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        firstLease.Should().NotBeNull();

        await service.ReleaseActorProjectionAsync(firstLease!);

        var secondLease = await service.EnsureActorProjectionAsync("root", "direct", "hello again", "cmd-2");
        secondLease.Should().NotBeNull();
    }

    [Fact]
    public async Task AttachLiveSinkAsync_ShouldNotOverwriteRunMetadata()
    {
        var initialStartedAt = new DateTimeOffset(2026, 2, 19, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableProjectionClock(initialStartedAt);
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out var store,
            clock);

        var lease = await service.EnsureActorProjectionAsync("root", "wf", "original-input", "cmd-1");
        lease.Should().NotBeNull();

        var beforeAttach = await store.GetAsync("root");
        beforeAttach.Should().NotBeNull();
        beforeAttach!.CommandId.Should().Be("cmd-1");
        beforeAttach.WorkflowName.Should().Be("wf");
        beforeAttach.Input.Should().Be("original-input");
        beforeAttach.StartedAt.Should().Be(initialStartedAt);

        clock.UtcNow = initialStartedAt.AddMinutes(10);
        var sink = new WorkflowRunEventChannel();
        await service.AttachLiveSinkAsync(lease!, sink);

        var afterAttach = await store.GetAsync("root");
        afterAttach.Should().NotBeNull();
        afterAttach!.CommandId.Should().Be("cmd-1");
        afterAttach.WorkflowName.Should().Be("wf");
        afterAttach.Input.Should().Be("original-input");
        afterAttach.StartedAt.Should().Be(initialStartedAt);
        await service.DetachLiveSinkAsync(lease!, sink);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_ShouldStopReceivingNewEvents()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams,
            out _);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));

        await WaitUntilAsync(async () =>
        {
            var timelineItems = await service.ListActorTimelineAsync("root", 50);
            return timelineItems.Any(x => x.Stage == "workflow.start");
        });

        var beforeRelease = await service.ListActorTimelineAsync("root", 50);
        beforeRelease.Should().ContainSingle(x => x.Stage == "workflow.start");

        await service.ReleaseActorProjectionAsync(lease!);
        await streams.GetStream("root").ProduceAsync(Wrap(new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }));

        await Task.Delay(50);
        var afterRelease = await service.ListActorTimelineAsync("root", 50);
        afterRelease.Count(x => x.Stage == "step.request").Should().Be(0);
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_WhenLiveSinkAttached_ShouldKeepProjectionActive()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams,
            out _);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        var sink = new WorkflowRunEventChannel();
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.ReleaseActorProjectionAsync(lease!);
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));

        await WaitUntilAsync(async () =>
        {
            var timelineItems = await service.ListActorTimelineAsync("root", 50);
            return timelineItems.Any(x => x.Stage == "workflow.start");
        });

        await service.DetachLiveSinkAsync(lease!, sink);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkBackpressure_ShouldPublishRunErrorAndDetachFailingSink()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _,
            out var runEventHub);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();

        var failingSink = new BackpressureFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, failingSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowRunStartedEvent
        {
            ThreadId = "thread-1",
        });

        await WaitUntilAsync(() => Task.FromResult(
            recordingSink.SnapshotEvents()
                .OfType<WorkflowRunErrorEvent>()
                .Any(x => x.Code == "RUN_SINK_BACKPRESSURE")));

        failingSink.PushAsyncCallCount.Should().Be(1);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowStepStartedEvent
        {
            StepName = "step-2",
        });

        await Task.Delay(50);
        failingSink.PushAsyncCallCount.Should().Be(1);

        var errorEvent = recordingSink.SnapshotEvents()
            .OfType<WorkflowRunErrorEvent>()
            .Single(x => x.Code == "RUN_SINK_BACKPRESSURE");
        errorEvent.Message.Should().Contain("eventType=RUN_STARTED");

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    private static WorkflowExecutionProjectionService CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out InMemoryWorkflowExecutionReadModelStore store,
        IProjectionClock? clock = null)
    {
        return CreateService(
            options,
            out streams,
            out store,
            out _,
            clock);
    }

    private static WorkflowExecutionProjectionService CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out InMemoryWorkflowExecutionReadModelStore store,
        out IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
        IProjectionClock? clock = null)
    {
        streams = new InMemoryStreamProvider();
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        store = new InMemoryWorkflowExecutionReadModelStore();
        var projector = new WorkflowExecutionReadModelProjector(
            store,
            new TestEventDeduplicator(),
            BuildReducers());
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>([projector]);
        var dispatcher = new ProjectionDispatcher<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator);
        var runRegistry = new ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>(
            dispatcher,
            subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            coordinator,
            dispatcher,
            runRegistry);

        // Use a dedicated local actor runtime for projection coordinator actors.
        var runtimeServices = new ServiceCollection();
        var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, runtimeProvider, streams);
        var ownershipCoordinator = new ActorProjectionOwnershipCoordinator(runtime);
        runEventStreamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());

        return new WorkflowExecutionProjectionService(
            ownershipCoordinator,
            options,
            lifecycle,
            store,
            clock ?? new SystemProjectionClock(),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            runEventStreamHub,
            new WorkflowExecutionReadModelMapper());
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

    private static async Task<(IWorkflowExecutionProjectionLease? Lease, Exception? Error)> CaptureLeaseOutcomeAsync(
        Task<IWorkflowExecutionProjectionLease?> task)
    {
        try
        {
            var lease = await task;
            return (lease, null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private sealed class MutableProjectionClock : IProjectionClock
    {
        public MutableProjectionClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
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

    private sealed class BackpressureFailingSink : IWorkflowRunEventSink
    {
        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new WorkflowRunEventSinkBackpressureException();
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            PushAsyncCallCount++;
            throw new WorkflowRunEventSinkBackpressureException();
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWorkflowRunEventSink : IWorkflowRunEventSink
    {
        private readonly object _gate = new();
        private readonly List<WorkflowRunEvent> _events = [];

        public IReadOnlyList<WorkflowRunEvent> SnapshotEvents()
        {
            lock (_gate)
                return _events.ToList();
        }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            lock (_gate)
                _events.Add(evt);
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                _events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
