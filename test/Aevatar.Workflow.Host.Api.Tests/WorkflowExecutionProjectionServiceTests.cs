using Aevatar.AI.Projection.Reducers;
using Aevatar.AI.Projection.Appliers;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
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
            out var store);

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

        await store.WaitForTimelineStageAsync("root", "workflow.start", TimeSpan.FromSeconds(2));

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

        var sink = new EventChannel<WorkflowRunEvent>();
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
        beforeAttach.CreatedAt.Should().Be(initialStartedAt);
        beforeAttach.UpdatedAt.Should().Be(initialStartedAt);
        beforeAttach.StartedAt.Should().Be(initialStartedAt);

        clock.UtcNow = initialStartedAt.AddMinutes(10);
        var sink = new EventChannel<WorkflowRunEvent>();
        await service.AttachLiveSinkAsync(lease!, sink);

        var afterAttach = await store.GetAsync("root");
        afterAttach.Should().NotBeNull();
        afterAttach!.CommandId.Should().Be("cmd-1");
        afterAttach.WorkflowName.Should().Be("wf");
        afterAttach.Input.Should().Be("original-input");
        afterAttach.CreatedAt.Should().Be(initialStartedAt);
        afterAttach.UpdatedAt.Should().Be(initialStartedAt);
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
            out var store);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));

        await store.WaitForTimelineStageAsync("root", "workflow.start", TimeSpan.FromSeconds(2));

        var beforeRelease = await service.ListActorTimelineAsync("root", 50);
        beforeRelease.Should().ContainSingle(x => x.Stage == "workflow.start");

        await service.ReleaseActorProjectionAsync(lease!);
        await WaitForProducedEventDispatchAsync<StepRequestEvent>(
            streams,
            "root",
            () => streams.GetStream("root").ProduceAsync(Wrap(new StepRequestEvent
            {
                StepId = "s1",
                StepType = "llm_call",
                TargetRole = "assistant",
            })),
            TimeSpan.FromSeconds(2));
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
            out var store);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        var sink = new EventChannel<WorkflowRunEvent>();
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.ReleaseActorProjectionAsync(lease!);
        await streams.GetStream("root").ProduceAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }));

        await store.WaitForTimelineStageAsync("root", "workflow.start", TimeSpan.FromSeconds(2));

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

        await recordingSink.WaitForEventAsync(
            evt => evt is WorkflowRunErrorEvent x && x.Code == "RUN_SINK_BACKPRESSURE",
            TimeSpan.FromSeconds(2));

        failingSink.PushAsyncCallCount.Should().Be(1);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowStepStartedEvent
        {
            StepName = "step-2",
        });

        await recordingSink.WaitForEventAsync(
            evt => evt is WorkflowStepStartedEvent x && x.StepName == "step-2",
            TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(1);

        var errorEvent = recordingSink.SnapshotEvents()
            .OfType<WorkflowRunErrorEvent>()
            .Single(x => x.Code == "RUN_SINK_BACKPRESSURE");
        errorEvent.Message.Should().Contain("eventType=RUN_STARTED");

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkThrowsInvalidOperation_ShouldPublishRunErrorAndDetachFailingSink()
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

        var failingSink = new InvalidOperationFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, failingSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowRunStartedEvent
        {
            ThreadId = "thread-1",
        });

        await recordingSink.WaitForEventAsync(
            evt => evt is WorkflowRunErrorEvent x && x.Code == "RUN_SINK_WRITE_FAILED",
            TimeSpan.FromSeconds(2));

        failingSink.PushAsyncCallCount.Should().Be(1);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowStepStartedEvent
        {
            StepName = "step-2",
        });

        await recordingSink.WaitForEventAsync(
            evt => evt is WorkflowStepStartedEvent x && x.StepName == "step-2",
            TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(1);

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkCompleted_ShouldDetachWithoutPublishingRunError()
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

        var completedSink = new CompletedFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, completedSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowRunStartedEvent
        {
            ThreadId = "thread-1",
        });
        await completedSink.WaitForCallCountAsync(1, TimeSpan.FromSeconds(2));

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowStepStartedEvent
        {
            StepName = "step-2",
        });
        await recordingSink.WaitForEventAsync(
            evt => evt is WorkflowStepStartedEvent x && x.StepName == "step-2",
            TimeSpan.FromSeconds(2));

        completedSink.PushAsyncCallCount.Should().Be(1);
        recordingSink.SnapshotEvents().OfType<WorkflowRunErrorEvent>().Should().BeEmpty();

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSameSinkAttachedTwice_ShouldReplaceSubscriptionInsteadOfDuplicating()
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

        var sink = new RecordingWorkflowRunEventSink();
        var probeSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.AttachLiveSinkAsync(lease!, probeSink);

        await runEventHub.PublishAsync("root", "cmd-1", new WorkflowRunStartedEvent
        {
            ThreadId = "thread-1",
        });
        await probeSink.WaitForEventAsync(
            evt => evt is WorkflowRunStartedEvent,
            TimeSpan.FromSeconds(2));

        sink.SnapshotEvents().Should().HaveCount(1);

        await service.DetachLiveSinkAsync(lease!, probeSink);
        await service.DetachLiveSinkAsync(lease!, sink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenLifecycleStartFails_ShouldReleaseOwnership()
    {
        var ownership = new TrackingOwnershipCoordinator();
        var lifecycle = new ThrowingLifecycleService(new InvalidOperationException("start failed"));
        var service = CreateServiceForStartFailure(ownership, lifecycle);

        var act = async () => await service.EnsureActorProjectionAsync("root", "wf", "input", "cmd-1");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("start failed");

        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("root", "cmd-1"));
        ownership.Released.Should().ContainSingle().Which.Should().Be(("root", "cmd-1"));
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenLifecycleStartAndReleaseFail_ShouldPreserveOriginalException()
    {
        var ownership = new TrackingOwnershipCoordinator { ThrowOnRelease = true };
        var lifecycle = new ThrowingLifecycleService(new InvalidOperationException("start failed"));
        var service = CreateServiceForStartFailure(ownership, lifecycle);

        var act = async () => await service.EnsureActorProjectionAsync("root", "wf", "input", "cmd-1");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("start failed");

        ownership.Acquired.Should().ContainSingle();
        ownership.Released.Should().ContainSingle();
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenLeaseImplementationUnsupported_ShouldThrow()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _);
        var sink = new EventChannel<WorkflowRunEvent>();

        var act = async () => await service.AttachLiveSinkAsync(new ExternalLease("root", "cmd"), sink);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported projection lease type*WorkflowExecutionRuntimeLease*");

        await sink.DisposeAsync();
    }

    private static ProjectionPortsHarness CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out ObservableWorkflowExecutionDocumentStore store,
        IProjectionClock? clock = null)
    {
        return CreateService(
            options,
            out streams,
            out store,
            out _,
            clock);
    }

    private static ProjectionPortsHarness CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out ObservableWorkflowExecutionDocumentStore store,
        out IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
        IProjectionClock? clock = null)
    {
        var forwardingRegistry = new InMemoryStreamForwardingRegistry();
        streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            forwardingRegistry);
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        store = new ObservableWorkflowExecutionDocumentStore();
        var resolvedClock = clock ?? new SystemProjectionClock();
        var relationStore = new InMemoryProjectionGraphStore();
        var bindings = new IProjectionStoreBinding<WorkflowExecutionReport, string>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowExecutionReport, string>(store),
            new ProjectionGraphStoreBinding<WorkflowExecutionReport, string>(relationStore),
        };
        var storeDispatcher = new ProjectionStoreDispatcher<WorkflowExecutionReport, string>(bindings);
        var projector = new WorkflowExecutionReadModelProjector(
            storeDispatcher,
            new TestEventDeduplicator(),
            resolvedClock,
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
        runtimeServices.AddSingleton<IAgentManifestStore, InMemoryManifestStore>();
        runtimeServices.AddSingleton<IEventStore, InMemoryEventStore>();
        runtimeServices.AddSingleton<EventSourcingRuntimeOptions>();
        runtimeServices.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtime = new LocalActorRuntime(
            streams,
            runtimeProvider,
            streams);
        var runtimeTypeProbe = new RuntimeActorTypeProbe(runtime);
        var ownershipTypeVerifier = new DefaultAgentTypeVerifier(
            runtimeTypeProbe,
            runtimeProvider.GetRequiredService<IAgentManifestStore>());
        var ownershipCoordinator = new ActorProjectionOwnershipCoordinator(
            runtime,
            ownershipTypeVerifier);
        runEventStreamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());
        var mapper = new WorkflowExecutionReadModelMapper();
        var sinkManager = new EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEvent>(runEventStreamHub);
        var sinkFailurePolicy = new WorkflowProjectionSinkFailurePolicy(sinkManager, runEventStreamHub, resolvedClock);
        var readModelUpdater = new WorkflowProjectionReadModelUpdater(storeDispatcher, resolvedClock);
        var queryReader = new WorkflowProjectionQueryReader(
            store,
            mapper,
            relationStore);
        var activationService = new WorkflowProjectionActivationService(
            lifecycle,
            resolvedClock,
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownershipCoordinator,
            readModelUpdater);
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            readModelUpdater,
            ownershipCoordinator);
        var liveSinkForwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEvent>(sinkFailurePolicy);

        var lifecyclePort = new WorkflowExecutionProjectionLifecycleService(
            options,
            activationService,
            releaseService,
            sinkManager,
            liveSinkForwarder);
        var queryPort = new WorkflowExecutionProjectionQueryService(
            options,
            queryReader);
        return new ProjectionPortsHarness(lifecyclePort, queryPort);
    }

    private static ProjectionPortsHarness CreateServiceForStartFailure(
        IProjectionOwnershipCoordinator ownershipCoordinator,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle)
    {
        var store = CreateStore();
        var clock = new SystemProjectionClock();
        var relationStore = new InMemoryProjectionGraphStore();
        var bindings = new IProjectionStoreBinding<WorkflowExecutionReport, string>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowExecutionReport, string>(store),
            new ProjectionGraphStoreBinding<WorkflowExecutionReport, string>(relationStore),
        };
        var storeDispatcher = new ProjectionStoreDispatcher<WorkflowExecutionReport, string>(bindings);
        var runEventHub = new NoOpWorkflowRunEventHub();
        var mapper = new WorkflowExecutionReadModelMapper();
        var sinkManager = new EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEvent>(runEventHub);
        var sinkFailurePolicy = new WorkflowProjectionSinkFailurePolicy(sinkManager, runEventHub, clock);
        var readModelUpdater = new WorkflowProjectionReadModelUpdater(storeDispatcher, clock);
        var queryReader = new WorkflowProjectionQueryReader(
            store,
            mapper,
            relationStore);
        var activationService = new WorkflowProjectionActivationService(
            lifecycle,
            clock,
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownershipCoordinator,
            readModelUpdater);
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            readModelUpdater,
            ownershipCoordinator);
        var liveSinkForwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEvent>(sinkFailurePolicy);

        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        };
        var lifecyclePort = new WorkflowExecutionProjectionLifecycleService(
            options,
            activationService,
            releaseService,
            sinkManager,
            liveSinkForwarder);
        var queryPort = new WorkflowExecutionProjectionQueryService(
            options,
            queryReader);
        return new ProjectionPortsHarness(lifecyclePort, queryPort);
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

    private static InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        listSortSelector: report => report.StartedAt);

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "root") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Down,
    };

    private static async Task WaitForProducedEventDispatchAsync<TEvent>(
        InMemoryStreamProvider streams,
        string streamId,
        Func<Task> publishAsync,
        TimeSpan timeout)
        where TEvent : IMessage, new()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var probe = await streams.GetStream(streamId).SubscribeAsync<TEvent>(_ =>
        {
            observed.TrySetResult(true);
            return Task.CompletedTask;
        });

        await publishAsync();
        await observed.Task.WaitAsync(timeout);
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

    private sealed class ProjectionPortsHarness
        : IWorkflowExecutionProjectionLifecyclePort,
          IWorkflowExecutionProjectionQueryPort
    {
        private readonly IWorkflowExecutionProjectionLifecyclePort _lifecyclePort;
        private readonly IWorkflowExecutionProjectionQueryPort _queryPort;

        public ProjectionPortsHarness(
            IWorkflowExecutionProjectionLifecyclePort lifecyclePort,
            IWorkflowExecutionProjectionQueryPort queryPort)
        {
            _lifecyclePort = lifecyclePort;
            _queryPort = queryPort;
        }

        public bool ProjectionEnabled => _lifecyclePort.ProjectionEnabled;

        public bool EnableActorQueryEndpoints => _queryPort.EnableActorQueryEndpoints;

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default)
            => _lifecyclePort.EnsureActorProjectionAsync(rootActorId, workflowName, input, commandId, ct);

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
            => _lifecyclePort.AttachLiveSinkAsync(lease, sink, ct);

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
            => _lifecyclePort.DetachLiveSinkAsync(lease, sink, ct);

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
            => _lifecyclePort.ReleaseActorProjectionAsync(lease, ct);

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
            string actorId,
            CancellationToken ct = default)
            => _queryPort.GetActorSnapshotAsync(actorId, ct);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
            int take = 200,
            CancellationToken ct = default)
            => _queryPort.ListActorSnapshotsAsync(take, ct);

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default)
            => _queryPort.ListActorTimelineAsync(actorId, take, ct);

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
            => _queryPort.GetActorGraphEdgesAsync(actorId, take, options, ct);

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
            => _queryPort.GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
            => _queryPort.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, options, ct);
    }

    private sealed class ObservableWorkflowExecutionDocumentStore
        : IProjectionDocumentStore<WorkflowExecutionReport, string>
    {
        private readonly InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> _inner = CreateStore();
        private readonly object _gate = new();
        private readonly List<StoreWaiter> _waiters = [];

        public async Task UpsertAsync(WorkflowExecutionReport report, CancellationToken ct = default)
        {
            await _inner.UpsertAsync(report, ct);
            await NotifyWaitersAsync(report.RootActorId, ct);
        }

        public async Task MutateAsync(string actorId, Action<WorkflowExecutionReport> mutate, CancellationToken ct = default)
        {
            await _inner.MutateAsync(actorId, mutate, ct);
            await NotifyWaitersAsync(actorId, ct);
        }

        public Task<WorkflowExecutionReport?> GetAsync(string actorId, CancellationToken ct = default) =>
            _inner.GetAsync(actorId, ct);

        public Task<IReadOnlyList<WorkflowExecutionReport>> ListAsync(int take = 50, CancellationToken ct = default) =>
            _inner.ListAsync(take, ct);

        public async Task WaitForTimelineStageAsync(string actorId, string stage, TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);

            if (await HasTimelineStageAsync(actorId, stage))
                return;

            var waiter = new StoreWaiter(
                actorId,
                report => report?.Timeline.Any(x => string.Equals(x.Stage, stage, StringComparison.Ordinal)) == true);
            Register(waiter);

            try
            {
                if (await HasTimelineStageAsync(actorId, stage))
                {
                    waiter.Signal.TrySetResult(true);
                }

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
            var report = await _inner.GetAsync(actorId, ct);
            List<StoreWaiter> ready;
            lock (_gate)
            {
                ready = _waiters
                    .Where(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal) && x.Predicate(report))
                    .ToList();

                foreach (var waiter in ready)
                    _waiters.Remove(waiter);
            }

            foreach (var waiter in ready)
                waiter.Signal.TrySetResult(true);
        }

        private sealed record StoreWaiter(
            string ActorId,
            Func<WorkflowExecutionReport?, bool> Predicate)
        {
            public TaskCompletionSource<bool> Signal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
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

    private sealed class BackpressureFailingSink : IEventSink<WorkflowRunEvent>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new EventSinkBackpressureException();
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            RecordCall();
            throw new EventSinkBackpressureException();
        }

        public Task WaitForCallCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (PushAsyncCallCount >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
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

        private void RecordCall()
        {
            lock (_gate)
            {
                PushAsyncCallCount++;
                for (var i = _countWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _countWaiters[i];
                    if (PushAsyncCallCount < waiter.Count)
                        continue;

                    _countWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
        }
    }

    private sealed class InvalidOperationFailingSink : IEventSink<WorkflowRunEvent>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new InvalidOperationException("sink write failed");
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            RecordCall();
            throw new InvalidOperationException("sink write failed");
        }

        public Task WaitForCallCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (PushAsyncCallCount >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
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

        private void RecordCall()
        {
            lock (_gate)
            {
                PushAsyncCallCount++;
                for (var i = _countWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _countWaiters[i];
                    if (PushAsyncCallCount < waiter.Count)
                        continue;

                    _countWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
        }
    }

    private sealed class CompletedFailingSink : IEventSink<WorkflowRunEvent>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new EventSinkCompletedException();
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            RecordCall();
            throw new EventSinkCompletedException();
        }

        public Task WaitForCallCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (PushAsyncCallCount >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
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

        private void RecordCall()
        {
            lock (_gate)
            {
                PushAsyncCallCount++;
                for (var i = _countWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _countWaiters[i];
                    if (PushAsyncCallCount < waiter.Count)
                        continue;

                    _countWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
        }
    }

    private sealed class RecordingWorkflowRunEventSink : IEventSink<WorkflowRunEvent>
    {
        private readonly object _gate = new();
        private readonly List<WorkflowRunEvent> _events = [];
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];
        private readonly List<(Func<WorkflowRunEvent, bool> Predicate, TaskCompletionSource<bool> Signal)> _predicateWaiters = [];

        public IReadOnlyList<WorkflowRunEvent> SnapshotEvents()
        {
            lock (_gate)
                return _events.ToList();
        }

        public Task WaitForEventCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_events.Count >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
        }

        public Task WaitForEventAsync(Func<WorkflowRunEvent, bool> predicate, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            Task waitTask;
            lock (_gate)
            {
                if (_events.Any(predicate))
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _predicateWaiters.Add((predicate, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
        }

        public void Push(WorkflowRunEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            Append(evt);
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            Append(evt);
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

        private void Append(WorkflowRunEvent evt)
        {
            lock (_gate)
            {
                _events.Add(evt);

                for (var i = _countWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _countWaiters[i];
                    if (_events.Count < waiter.Count)
                        continue;

                    _countWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }

                for (var i = _predicateWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _predicateWaiters[i];
                    if (!waiter.Predicate(evt))
                        continue;

                    _predicateWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
        }
    }

    private sealed class TrackingOwnershipCoordinator : IProjectionOwnershipCoordinator
    {
        public bool ThrowOnRelease { get; init; }
        public List<(string ScopeId, string SessionId)> Acquired { get; } = [];
        public List<(string ScopeId, string SessionId)> Released { get; } = [];

        public Task AcquireAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            Acquired.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            Released.Add((scopeId, sessionId));
            if (ThrowOnRelease)
                throw new InvalidOperationException("release failed");

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLifecycleService(Exception ex)
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default) => throw ex;
        public Task ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(WorkflowExecutionProjectionContext context, IReadOnlyList<WorkflowExecutionTopologyEdge> completion, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpWorkflowRunEventHub : IProjectionSessionEventHub<WorkflowRunEvent>
    {
        public Task PublishAsync(string scopeId, string sessionId, WorkflowRunEvent evt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEvent, ValueTask> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExternalLease(string actorId, string commandId) : IWorkflowExecutionProjectionLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
    }

    private sealed class RuntimeActorTypeProbe : IActorTypeProbe
    {
        private readonly IActorRuntime _runtime;

        public RuntimeActorTypeProbe(IActorRuntime runtime)
        {
            _runtime = runtime;
        }

        public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            var actor = await _runtime.GetAsync(actorId);
            var runtimeType = actor?.Agent.GetType();
            return runtimeType?.AssemblyQualifiedName ?? runtimeType?.FullName;
        }
    }
}
