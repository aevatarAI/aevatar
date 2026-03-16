using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
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
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Host.Api.Tests;

[CollectionDefinition(nameof(WorkflowExecutionProjectionServiceSerialCollection), DisableParallelization = true)]
public sealed class WorkflowExecutionProjectionServiceSerialCollection;

[Collection(nameof(WorkflowExecutionProjectionServiceSerialCollection))]
public class WorkflowExecutionProjectionServiceTests
{
    private static readonly WorkflowRunGraphMirrorMaterializer GraphMaterializer = new();

    [Fact]
    public async Task EnsureActorProjectionAsync_WhenEnabled_ShouldExposeActorSnapshotAndTimeline()
    {
        const string actorId = "root-ensure-projection";
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams,
            out var store);

        var lease = await service.EnsureActorProjectionAsync(actorId, "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        await streams.GetStream(actorId).ProduceAsync(WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, version: 1, publisherId: actorId));
        await streams.GetStream(actorId).ProduceAsync(WrapCommitted(new WorkflowCompletedEvent
        {
            WorkflowName = "direct",
            Success = true,
            Output = "done",
        }, version: 2, publisherId: actorId));

        await store.WaitForTimelineStageAsync(actorId, "workflow.start", TimeSpan.FromSeconds(5));

        var snapshot = await service.GetActorSnapshotAsync(actorId);
        var projectionState = await service.GetActorProjectionStateAsync(actorId);
        var timeline = await service.ListActorTimelineAsync(actorId, 50);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be(actorId);
        projectionState.Should().NotBeNull();
        projectionState!.LastCommandId.Should().Be("cmd-1");
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

        var sink = new EventChannel<WorkflowRunEventEnvelope>();
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
    public async Task AttachLiveSinkAsync_ShouldNotCreateReportArtifactWithoutCommittedEvents()
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
            out var reportStore,
            out _,
            out _,
            clock);

        var lease = await service.EnsureActorProjectionAsync("root", "wf", "original-input", "cmd-1");
        lease.Should().NotBeNull();

        var beforeAttach = await reportStore.GetAsync("root");
        beforeAttach.Should().BeNull();

        clock.UtcNow = initialStartedAt.AddMinutes(10);
        var sink = new EventChannel<WorkflowRunEventEnvelope>();
        await service.AttachLiveSinkAsync(lease!, sink);

        var afterAttach = await reportStore.GetAsync("root");
        afterAttach.Should().BeNull();
        await service.DetachLiveSinkAsync(lease!, sink);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_ShouldStopReceivingNewEvents()
    {
        const string actorId = "root-release-projection";
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams,
            out var store);

        var lease = await service.EnsureActorProjectionAsync(actorId, "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        await streams.GetStream(actorId).ProduceAsync(WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, version: 1, publisherId: actorId));

        await store.WaitForTimelineStageAsync(actorId, "workflow.start", TimeSpan.FromSeconds(5));

        var beforeRelease = await service.ListActorTimelineAsync(actorId, 50);
        beforeRelease.Should().ContainSingle(x => x.Stage == "workflow.start");

        await service.ReleaseActorProjectionAsync(lease!);
        await WaitForProducedEventDispatchAsync<StepRequestEvent>(
            streams,
            actorId,
            () => streams.GetStream(actorId).ProduceAsync(WrapCommitted(new StepRequestEvent
            {
                StepId = "s1",
                StepType = "llm_call",
                TargetRole = "assistant",
            }, version: 2, publisherId: actorId)),
            TimeSpan.FromSeconds(2));
        var afterRelease = await service.ListActorTimelineAsync(actorId, 50);
        afterRelease.Count(x => x.Stage == "step.request").Should().Be(0);
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_WhenLiveSinkAttached_ShouldKeepProjectionActive()
    {
        const string actorId = "root-live-sink";
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out var streams,
            out var store);

        var lease = await service.EnsureActorProjectionAsync(actorId, "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();
        var sink = new EventChannel<WorkflowRunEventEnvelope>();
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.ReleaseActorProjectionAsync(lease!);
        await streams.GetStream(actorId).ProduceAsync(WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "direct",
            Input = "hello",
        }, version: 1, publisherId: actorId));

        await store.WaitForTimelineStageAsync(actorId, "workflow.start", TimeSpan.FromSeconds(5));

        await service.DetachLiveSinkAsync(lease!, sink);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkBackpressure_ShouldPublishCustomFailureAndDetachFailingSink()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _,
            out _,
            out var runEventHub);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();

        var failingSink = new BackpressureFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, failingSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", BuildRunStartedEvent("thread-1"));

        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom
                && evt.Custom.Name == WorkflowProjectionSinkFailurePolicy.ProjectionSinkFailureEventName
                && evt.Custom.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().Code == "RUN_SINK_BACKPRESSURE",
            TimeSpan.FromSeconds(2));

        await failingSink.WaitForCallCountAsync(2, TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(2);

        await runEventHub.PublishAsync("root", "cmd-1", BuildStepStartedEvent("step-2"));

        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.StepStarted
                && evt.StepStarted.StepName == "step-2",
            TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(2);

        var failureEvent = recordingSink.SnapshotEvents()
            .Where(x => x.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom)
            .Select(x => x.Custom)
            .Single(x => x.Name == WorkflowProjectionSinkFailurePolicy.ProjectionSinkFailureEventName);
        failureEvent.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().EventType
            .Should().Be(WorkflowRunEventTypes.RunStarted);

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkThrowsInvalidOperation_ShouldPublishCustomFailureAndDetachFailingSink()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _,
            out _,
            out var runEventHub);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();

        var failingSink = new InvalidOperationFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, failingSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", BuildRunStartedEvent("thread-1"));

        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom
                && evt.Custom.Name == WorkflowProjectionSinkFailurePolicy.ProjectionSinkFailureEventName
                && evt.Custom.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().Code == "RUN_SINK_WRITE_FAILED",
            TimeSpan.FromSeconds(2));

        await failingSink.WaitForCallCountAsync(2, TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(2);

        await runEventHub.PublishAsync("root", "cmd-1", BuildStepStartedEvent("step-2"));

        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.StepStarted
                && evt.StepStarted.StepName == "step-2",
            TimeSpan.FromSeconds(2));
        failingSink.PushAsyncCallCount.Should().Be(2);

        await service.DetachLiveSinkAsync(lease!, recordingSink);
        await service.ReleaseActorProjectionAsync(lease!);
    }

    [Fact]
    public async Task AttachLiveSinkAsync_WhenSinkCompleted_ShouldPublishCustomFailureAndDetachFailingSink()
    {
        var service = CreateService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            out _,
            out _,
            out _,
            out var runEventHub);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();

        var completedSink = new CompletedFailingSink();
        var recordingSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, completedSink);
        await service.AttachLiveSinkAsync(lease!, recordingSink);

        await runEventHub.PublishAsync("root", "cmd-1", BuildRunStartedEvent("thread-1"));
        await completedSink.WaitForCallCountAsync(2, TimeSpan.FromSeconds(2));

        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom
                && evt.Custom.Name == WorkflowProjectionSinkFailurePolicy.ProjectionSinkFailureEventName
                && evt.Custom.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().Code == "RUN_SINK_WRITE_FAILED",
            TimeSpan.FromSeconds(2));

        await runEventHub.PublishAsync("root", "cmd-1", BuildStepStartedEvent("step-2"));
        await recordingSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.StepStarted
                && evt.StepStarted.StepName == "step-2",
            TimeSpan.FromSeconds(2));

        completedSink.PushAsyncCallCount.Should().Be(2);
        recordingSink.SnapshotEvents()
            .Where(x => x.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom)
            .Should().ContainSingle();

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
            out _,
            out var runEventHub);

        var lease = await service.EnsureActorProjectionAsync("root", "direct", "hello", "cmd-1");
        lease.Should().NotBeNull();

        var sink = new RecordingWorkflowRunEventSink();
        var probeSink = new RecordingWorkflowRunEventSink();
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.AttachLiveSinkAsync(lease!, sink);
        await service.AttachLiveSinkAsync(lease!, probeSink);

        await runEventHub.PublishAsync("root", "cmd-1", BuildRunStartedEvent("thread-1"));
        await probeSink.WaitForEventAsync(
            evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunStarted,
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
        var sink = new EventChannel<WorkflowRunEventEnvelope>();

        var act = async () => await service.AttachLiveSinkAsync(new ExternalLease("root", "cmd"), sink);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported projection lease type*WorkflowExecutionRuntimeLease*");

        await sink.DisposeAsync();
    }

    private static ProjectionPortsHarness CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out ObservableWorkflowRunTimelineDocumentStore timelineStore,
        IProjectionClock? clock = null)
    {
        return CreateService(
            options,
            out streams,
            out _,
            out timelineStore,
            out _,
            clock);
    }

    private static ProjectionPortsHarness CreateService(
        WorkflowExecutionProjectionOptions options,
        out InMemoryStreamProvider streams,
        out ObservableWorkflowExecutionDocumentStore reportStore,
        out ObservableWorkflowRunTimelineDocumentStore timelineStore,
        out IProjectionSessionEventHub<WorkflowRunEventEnvelope> runEventStreamHub,
        IProjectionClock? clock = null)
    {
        var forwardingRegistry = new InMemoryStreamForwardingRegistry();
        streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            forwardingRegistry);
        var subscriptionHub = new ActorStreamSubscriptionHub<EventEnvelope>(streams);
        reportStore = new ObservableWorkflowExecutionDocumentStore();
        timelineStore = new ObservableWorkflowRunTimelineDocumentStore();
        var currentStateStore = CreateCurrentStateStore();
        var resolvedClock = clock ?? new SystemProjectionClock();
        var relationStore = new InMemoryProjectionGraphStore();
        var reportBindings = new IProjectionWriteSink<WorkflowRunInsightReportDocument>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowRunInsightReportDocument>(reportStore),
        };
        var reportStoreDispatcher = new ProjectionStoreDispatcher<WorkflowRunInsightReportDocument>(reportBindings);
        var timelineStoreDispatcher = new ProjectionStoreDispatcher<WorkflowRunTimelineDocument>(
            [new ProjectionDocumentStoreBinding<WorkflowRunTimelineDocument>(timelineStore)]);
        var graphStoreDispatcher = new ProjectionStoreDispatcher<WorkflowRunGraphMirrorReadModel>(
            [new ProjectionGraphStoreBinding<WorkflowRunGraphMirrorReadModel>(relationStore, GraphMaterializer)]);
        var currentStateDispatcher = new ProjectionStoreDispatcher<WorkflowExecutionCurrentStateDocument>(
            [new ProjectionDocumentStoreBinding<WorkflowExecutionCurrentStateDocument>(currentStateStore)]);
        var currentStateProjector = new WorkflowExecutionCurrentStateProjector(
            currentStateDispatcher,
            resolvedClock);

        // Use a dedicated local actor runtime for projection coordinator actors.
        var runtimeServices = new ServiceCollection();
        runtimeServices.AddSingleton<IStreamProvider>(streams);
        runtimeServices.AddSingleton<IEventStore, InMemoryEventStore>();
        runtimeServices.AddSingleton<EventSourcingRuntimeOptions>();
        runtimeServices.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        runtimeServices.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        runtimeServices.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtime = new LocalActorRuntime(
            streams,
            runtimeProvider,
            streams);
        var dispatchPort = new LocalActorDispatchPort(runtime);
        var runtimeTypeProbe = new RuntimeActorTypeProbe(runtime);
        var ownershipTypeVerifier = new DefaultAgentTypeVerifier(runtimeTypeProbe);
        var insightActorPort = new ActorWorkflowRunInsightPort(runtime, dispatchPort, ownershipTypeVerifier);
        var ownershipCoordinator = new ActorProjectionOwnershipCoordinator(
            runtime,
            dispatchPort,
            ownershipTypeVerifier,
            runtimeProvider.GetRequiredService<IEventStore>());
        var insightCoordinator = new ProjectionCoordinator<WorkflowRunInsightProjectionContext, bool>(
            [
                new WorkflowRunInsightReportDocumentProjector(reportStoreDispatcher),
                new WorkflowRunTimelineReadModelProjector(timelineStoreDispatcher),
                new WorkflowRunGraphMirrorProjector(graphStoreDispatcher),
            ]);
        var insightDispatcher = new ProjectionDispatcher<WorkflowRunInsightProjectionContext, bool>(insightCoordinator);
        var insightRegistry = new ProjectionSubscriptionRegistry<WorkflowRunInsightProjectionContext>(
            insightDispatcher,
            subscriptionHub);
        var insightLifecycle = new ProjectionLifecycleService<WorkflowRunInsightProjectionContext, bool>(
            insightCoordinator,
            insightDispatcher,
            insightRegistry);
        var insightActivation = new ContextProjectionActivationService<WorkflowRunInsightRuntimeLease, WorkflowRunInsightProjectionContext, bool>(
            insightLifecycle,
            (rootEntityId, _, _, _, _) => new WorkflowRunInsightProjectionContext
            {
                ProjectionId = $"{rootEntityId}:insight",
                RootActorId = WorkflowRunInsightGAgent.BuildActorId(rootEntityId),
                RunActorId = rootEntityId,
            },
            context => new WorkflowRunInsightRuntimeLease(context));
        var projector = new WorkflowRunInsightBridgeProjector(
            insightActorPort,
            insightActivation,
            new TestEventDeduplicator(),
            resolvedClock);
        var coordinator = new ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            [currentStateProjector, projector]);
        var dispatcher = new ProjectionDispatcher<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(coordinator);
        var runRegistry = new ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>(
            dispatcher,
            subscriptionHub);
        var lifecycle = new ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            coordinator,
            dispatcher,
            runRegistry);
        runEventStreamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var mapper = new WorkflowExecutionReadModelMapper();
        var sinkManager = new EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(runEventStreamHub);
        var sinkFailurePolicy = new WorkflowProjectionSinkFailurePolicy(sinkManager, runEventStreamHub, resolvedClock);
        var activationService = CreateActivationService(
            lifecycle,
            resolvedClock,
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownershipCoordinator);
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);
        var liveSinkForwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(sinkFailurePolicy);

        var projectionPort = new WorkflowExecutionProjectionPort(
            options,
            activationService,
            releaseService,
            sinkManager,
            liveSinkForwarder);
        var queryPort = new WorkflowProjectionQueryReader(
            reportStore,
            currentStateStore,
            timelineStore,
            mapper,
            relationStore,
            options);
        return new ProjectionPortsHarness(projectionPort, queryPort);
    }

    private static ProjectionPortsHarness CreateServiceForStartFailure(
        IProjectionOwnershipCoordinator ownershipCoordinator,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle)
    {
        var reportStore = CreateStore();
        var timelineStore = CreateTimelineStore();
        var currentStateStore = CreateCurrentStateStore();
        var clock = new SystemProjectionClock();
        var relationStore = new InMemoryProjectionGraphStore();
        var reportBindings = new IProjectionWriteSink<WorkflowRunInsightReportDocument>[]
        {
            new ProjectionDocumentStoreBinding<WorkflowRunInsightReportDocument>(reportStore),
        };
        _ = new ProjectionStoreDispatcher<WorkflowRunInsightReportDocument>(reportBindings);
        var runEventHub = new NoOpWorkflowRunEventHub();
        var mapper = new WorkflowExecutionReadModelMapper();
        var sinkManager = new EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(runEventHub);
        var sinkFailurePolicy = new WorkflowProjectionSinkFailurePolicy(sinkManager, runEventHub, clock);
        var activationService = CreateActivationService(
            lifecycle,
            clock,
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownershipCoordinator);
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);
        var liveSinkForwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(sinkFailurePolicy);

        var options = new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        };
        var projectionPort = new WorkflowExecutionProjectionPort(
            options,
            activationService,
            releaseService,
            sinkManager,
            liveSinkForwarder);
        var queryPort = new WorkflowProjectionQueryReader(
            reportStore,
            currentStateStore,
            timelineStore,
            mapper,
            relationStore,
            options);
        return new ProjectionPortsHarness(projectionPort, queryPort);
    }

    private static ContextProjectionActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> CreateActivationService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IProjectionOwnershipCoordinator ownershipCoordinator,
        ProjectionOwnershipCoordinatorOptions? ownershipOptions = null,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent>? projectionControlHub = null) =>
        new(
            lifecycle,
            (rootEntityId, workflowName, input, commandId, _) => contextFactory.Create(
                rootEntityId,
                commandId,
                rootEntityId,
                workflowName,
                input,
                clock.UtcNow),
            context => new WorkflowExecutionRuntimeLease(
                context,
                ownershipCoordinator,
                ownershipOptions,
                lifecycle,
                projectionControlHub),
            acquireBeforeStart: (rootEntityId, _, _, commandId, ct) =>
                ownershipCoordinator.AcquireAsync(rootEntityId, commandId, ct),
            onRuntimeLeaseCreated: async (_, _, _, runtimeLease, ct) =>
            {
                try
                {
                    await runtimeLease.WaitForProjectionReleaseListenerReadyAsync(ct);
                }
                catch
                {
                    await runtimeLease.StopProjectionReleaseListenerAsync();
                    await runtimeLease.StopOwnershipHeartbeatAsync();
                    throw;
                }
            },
            cleanupOnStartFailure: async (rootEntityId, commandId) =>
            {
                try
                {
                    await ownershipCoordinator.ReleaseAsync(rootEntityId, commandId, CancellationToken.None);
                }
                catch
                {
                }
            });

    private static InMemoryProjectionDocumentStore<WorkflowRunInsightReportDocument, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: report => report.StartedAt);

    private static InMemoryProjectionDocumentStore<WorkflowRunTimelineDocument, string> CreateTimelineStore() => new(
        keySelector: document => document.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: document => document.UpdatedAt);

    private static InMemoryProjectionDocumentStore<WorkflowExecutionCurrentStateDocument, string> CreateCurrentStateStore() => new(
        keySelector: document => document.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: document => document.UpdatedAt);

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "root") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId, TopologyAudience.Children),
    };

    private static EventEnvelope WrapCommitted(IMessage evt, long version, string publisherId = "root")
    {
        var envelope = Wrap(evt, publisherId);
        envelope.Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = envelope.Id,
                Version = version,
                Timestamp = envelope.Timestamp?.Clone(),
                EventData = Any.Pack(evt),
            },
            StateRoot = Any.Pack(new WorkflowRunState()),
        });
        envelope.Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherId);
        return envelope;
    }

    private static WorkflowRunEventEnvelope BuildRunStartedEvent(string threadId) =>
        new()
        {
            RunStarted = new WorkflowRunStartedEventPayload
            {
                ThreadId = threadId,
            },
        };

    private static WorkflowRunEventEnvelope BuildStepStartedEvent(string stepName) =>
        new()
        {
            StepStarted = new WorkflowStepStartedEventPayload
            {
                StepName = stepName,
            },
        };

    private static async Task WaitForProducedEventDispatchAsync<TEvent>(
        InMemoryStreamProvider streams,
        string streamId,
        Func<Task> publishAsync,
        TimeSpan timeout)
        where TEvent : IMessage, new()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var probe = await streams.GetStream(streamId).SubscribeAsync<CommittedStateEventPublished>(published =>
        {
            if (published.StateEvent?.EventData?.Is(new TEvent().Descriptor) == true)
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
        : IWorkflowExecutionProjectionPort,
          IWorkflowExecutionProjectionQueryPort
    {
        private readonly IWorkflowExecutionProjectionPort _projectionPort;
        private readonly IWorkflowExecutionProjectionQueryPort _queryPort;

        public ProjectionPortsHarness(
            IWorkflowExecutionProjectionPort projectionPort,
            IWorkflowExecutionProjectionQueryPort queryPort)
        {
            _projectionPort = projectionPort;
            _queryPort = queryPort;
        }

        public bool ProjectionEnabled => _projectionPort.ProjectionEnabled;

        public bool EnableActorQueryEndpoints => _queryPort.EnableActorQueryEndpoints;

        public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
            string rootActorId,
            string workflowName,
            string input,
            string commandId,
            CancellationToken ct = default)
            => _projectionPort.EnsureActorProjectionAsync(rootActorId, workflowName, input, commandId, ct);

        public Task AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
            => _projectionPort.AttachLiveSinkAsync(lease, sink, ct);

        public Task DetachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
            => _projectionPort.DetachLiveSinkAsync(lease, sink, ct);

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default)
            => _projectionPort.ReleaseActorProjectionAsync(lease, ct);

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
            string actorId,
            CancellationToken ct = default)
            => _queryPort.GetActorSnapshotAsync(actorId, ct);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
            int take = 200,
            CancellationToken ct = default)
            => _queryPort.ListActorSnapshotsAsync(take, ct);

        public Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default)
            => _queryPort.GetActorProjectionStateAsync(actorId, ct);

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
        : IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>,
          IProjectionDocumentWriter<WorkflowRunInsightReportDocument>
    {
        private readonly InMemoryProjectionDocumentStore<WorkflowRunInsightReportDocument, string> _inner = CreateStore();
        private readonly object _gate = new();
        private readonly List<StoreWaiter> _waiters = [];

        public async Task<ProjectionWriteResult> UpsertAsync(WorkflowRunInsightReportDocument report, CancellationToken ct = default)
        {
            var result = await _inner.UpsertAsync(report, ct);
            if (result.IsApplied)
                await NotifyWaitersAsync(report.RootActorId, ct);
            return result;
        }

        public Task<WorkflowRunInsightReportDocument?> GetAsync(string actorId, CancellationToken ct = default) =>
            _inner.GetAsync(actorId, ct);

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);

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
            Func<WorkflowRunInsightReportDocument?, bool> Predicate)
        {
            public TaskCompletionSource<bool> Signal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ObservableWorkflowRunTimelineDocumentStore
        : IProjectionDocumentReader<WorkflowRunTimelineDocument, string>,
          IProjectionDocumentWriter<WorkflowRunTimelineDocument>
    {
        private readonly InMemoryProjectionDocumentStore<WorkflowRunTimelineDocument, string> _inner = CreateTimelineStore();
        private readonly object _gate = new();
        private readonly List<StoreWaiter> _waiters = [];

        public async Task<ProjectionWriteResult> UpsertAsync(WorkflowRunTimelineDocument document, CancellationToken ct = default)
        {
            var result = await _inner.UpsertAsync(document, ct);
            if (result.IsApplied)
                await NotifyWaitersAsync(document.RootActorId, ct);
            return result;
        }

        public Task<WorkflowRunTimelineDocument?> GetAsync(string actorId, CancellationToken ct = default) =>
            _inner.GetAsync(actorId, ct);

        public Task<ProjectionDocumentQueryResult<WorkflowRunTimelineDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);

        public async Task WaitForTimelineStageAsync(string actorId, string stage, TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);

            if (await HasTimelineStageAsync(actorId, stage))
                return;

            var waiter = new StoreWaiter(
                actorId,
                document => document?.Timeline.Any(x => string.Equals(x.Stage, stage, StringComparison.Ordinal)) == true);
            Register(waiter);

            try
            {
                if (await HasTimelineStageAsync(actorId, stage))
                    waiter.Signal.TrySetResult(true);

                await waiter.Signal.Task.WaitAsync(timeout);
            }
            finally
            {
                Unregister(waiter);
            }
        }

        private async Task<bool> HasTimelineStageAsync(string actorId, string stage)
        {
            var document = await _inner.GetAsync(actorId);
            return document?.Timeline.Any(x => string.Equals(x.Stage, stage, StringComparison.Ordinal)) == true;
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
            var document = await _inner.GetAsync(actorId, ct);
            List<StoreWaiter> ready;
            lock (_gate)
            {
                ready = _waiters
                    .Where(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal) && x.Predicate(document))
                    .ToList();

                foreach (var waiter in ready)
                    _waiters.Remove(waiter);
            }

            foreach (var waiter in ready)
                waiter.Signal.TrySetResult(true);
        }

        private sealed record StoreWaiter(
            string ActorId,
            Func<WorkflowRunTimelineDocument?, bool> Predicate)
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

    private sealed class BackpressureFailingSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEventEnvelope evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new EventSinkBackpressureException();
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
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

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
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

    private sealed class InvalidOperationFailingSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEventEnvelope evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new InvalidOperationException("sink write failed");
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
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

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
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

    private sealed class CompletedFailingSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public int PushAsyncCallCount { get; private set; }

        public void Push(WorkflowRunEventEnvelope evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            throw new EventSinkCompletedException();
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
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

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
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

    private sealed class RecordingWorkflowRunEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly object _gate = new();
        private readonly List<WorkflowRunEventEnvelope> _events = [];
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];
        private readonly List<(Func<WorkflowRunEventEnvelope, bool> Predicate, TaskCompletionSource<bool> Signal)> _predicateWaiters = [];

        public IReadOnlyList<WorkflowRunEventEnvelope> SnapshotEvents()
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

        public Task WaitForEventAsync(Func<WorkflowRunEventEnvelope, bool> predicate, TimeSpan timeout)
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

        public void Push(WorkflowRunEventEnvelope evt)
        {
            ArgumentNullException.ThrowIfNull(evt);
            Append(evt);
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ct.ThrowIfCancellationRequested();
            Append(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Append(WorkflowRunEventEnvelope evt)
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

        public Task<bool> HasActiveLeaseAsync(string scopeId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Acquired.Contains((scopeId, sessionId)) && !Released.Contains((scopeId, sessionId)));

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

    private sealed class NoOpWorkflowRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
    {
        public Task PublishAsync(string scopeId, string sessionId, WorkflowRunEventEnvelope evt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
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
