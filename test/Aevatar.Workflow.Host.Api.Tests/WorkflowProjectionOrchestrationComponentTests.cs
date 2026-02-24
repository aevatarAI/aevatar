using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionOrchestrationComponentTests
{
    [Fact]
    public async Task LeaseManager_ShouldForwardAcquireAndRelease()
    {
        var ownership = new TrackingOwnershipCoordinator();
        var manager = new WorkflowProjectionLeaseManager(ownership);

        await manager.AcquireAsync("actor-1", "cmd-1");
        await manager.ReleaseAsync("actor-1", "cmd-1");

        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-1", "cmd-1"));
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-1", "cmd-1"));
    }

    [Fact]
    public async Task ActivationService_ShouldStartProjectionAndReturnRuntimeLease()
    {
        var lifecycle = new RecordingLifecycleService();
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = new WorkflowProjectionActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            new WorkflowProjectionLeaseManager(ownership),
            readModelUpdater);

        var lease = await activationService.EnsureAsync(
            "actor-activation",
            "direct",
            "hello",
            "cmd-activation",
            CancellationToken.None);

        lease.ActorId.Should().Be("actor-activation");
        lease.CommandId.Should().Be("cmd-activation");
        lifecycle.StartedContexts.Should().ContainSingle();
        readModelUpdater.Refreshed.Should().ContainSingle();
        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-activation", "cmd-activation"));
    }

    [Fact]
    public async Task ActivationService_WhenStartFails_ShouldReleaseOwnershipAndRethrow()
    {
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = new WorkflowProjectionActivationService(
            new ThrowingLifecycleService(new InvalidOperationException("start failed")),
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            new WorkflowProjectionLeaseManager(ownership),
            new RecordingReadModelUpdater());

        var act = async () => await activationService.EnsureAsync(
            "actor-fail",
            "direct",
            "hello",
            "cmd-fail",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("start failed");
        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-fail", "cmd-fail"));
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-fail", "cmd-fail"));
    }

    [Fact]
    public async Task ReadModelUpdater_ShouldRefreshMetadataAndMarkStopped()
    {
        var startedAt = new DateTimeOffset(2026, 2, 21, 12, 0, 0, TimeSpan.Zero);
        var stoppedAt = startedAt.AddMinutes(3);
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-2",
            CommandId = "cmd-old",
            WorkflowName = "old-workflow",
            Input = "old-input",
            StartedAt = startedAt.AddMinutes(-5),
            EndedAt = startedAt.AddMinutes(-6),
            CompletionStatus = WorkflowExecutionCompletionStatus.Running,
        });
        var updater = new WorkflowProjectionReadModelUpdater(
            new ProjectionMaterializationRouter<WorkflowExecutionReport, string>(
                store,
                new ProjectionGraphMaterializer<WorkflowExecutionReport>(new InMemoryProjectionGraphStore())),
            new FixedClock(stoppedAt));
        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-1",
            RootActorId = "actor-2",
            CommandId = "cmd-new",
            WorkflowName = "new-workflow",
            Input = "new-input",
            StartedAt = startedAt,
        };

        await updater.RefreshMetadataAsync("actor-2", context);
        await store.MutateAsync("actor-2", report =>
        {
            report.CompletionStatus = WorkflowExecutionCompletionStatus.Running;
            report.EndedAt = report.StartedAt.AddSeconds(-1);
        });
        await updater.MarkStoppedAsync("actor-2");

        var report = await store.GetAsync("actor-2");
        report.Should().NotBeNull();
        report!.CommandId.Should().Be("cmd-new");
        report.WorkflowName.Should().Be("new-workflow");
        report.Input.Should().Be("new-input");
        report.StartedAt.Should().Be(startedAt);
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.EndedAt.Should().Be(stoppedAt);
        report.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task QueryReader_ShouldMapSnapshotsAndSortTimeline()
    {
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-3",
            CommandId = "cmd-3",
            WorkflowName = "direct",
            StartedAt = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 2, 21, 10, 5, 0, TimeSpan.Zero),
            Success = true,
            FinalOutput = "done",
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 1, TimeSpan.Zero),
                    Stage = "step-1",
                },
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 3, TimeSpan.Zero),
                    Stage = "step-3",
                },
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 2, TimeSpan.Zero),
                    Stage = "step-2",
                },
            ],
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 3,
                RequestedSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 1,
            },
        });
        var reader = new WorkflowProjectionQueryReader(
            store,
            new WorkflowExecutionReadModelMapper(),
            new InMemoryProjectionGraphStore());

        var snapshot = await reader.GetActorSnapshotAsync("actor-3");
        var timeline = await reader.ListActorTimelineAsync("actor-3", take: 2);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("actor-3");
        snapshot.LastCommandId.Should().Be("cmd-3");
        snapshot.LastOutput.Should().Be("done");
        snapshot.TotalSteps.Should().Be(3);
        timeline.Should().HaveCount(2);
        timeline[0].Stage.Should().Be("step-3");
        timeline[1].Stage.Should().Be("step-2");
    }

    [Fact]
    public async Task SinkSubscriptionManager_ShouldReplaceSameSinkSubscription()
    {
        var hub = new RecordingRunEventHub();
        var manager = new WorkflowProjectionSinkSubscriptionManager(hub);
        var lease = CreateLease("actor-4", "cmd-4");
        var sink = new NoopRunEventSink();

        await manager.AttachOrReplaceAsync(lease, sink, _ => ValueTask.CompletedTask);
        var first = hub.Subscriptions.Should().ContainSingle().Subject;
        manager.GetSubscriptionCount(lease).Should().Be(1);

        await manager.AttachOrReplaceAsync(lease, sink, _ => ValueTask.CompletedTask);
        hub.Subscriptions.Should().HaveCount(2);
        first.Disposed.Should().BeTrue();
        manager.GetSubscriptionCount(lease).Should().Be(1);

        var second = hub.Subscriptions[1];
        await manager.DetachAsync(lease, sink);
        second.Disposed.Should().BeTrue();
        manager.GetSubscriptionCount(lease).Should().Be(0);
    }

    [Fact]
    public async Task SinkFailurePolicy_ShouldHandleBackpressureAndCompletedAndUnknownErrors()
    {
        var sinkManager = new RecordingSinkSubscriptionManager();
        var runEventHub = new RecordingRunEventHub();
        var policy = new WorkflowProjectionSinkFailurePolicy(
            sinkManager,
            runEventHub,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 9, 0, 0, TimeSpan.Zero)));
        var lease = CreateLease("actor-5", "cmd-5");
        var sink = new NoopRunEventSink();
        var sourceEvent = new WorkflowRunStartedEvent { ThreadId = "thread-1" };

        var handledBackpressure = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new WorkflowRunEventSinkBackpressureException());

        handledBackpressure.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
        runEventHub.PublishedEvents.Should().ContainSingle();
        runEventHub.PublishedEvents[0].evt.Should().BeOfType<WorkflowRunErrorEvent>();
        var backpressureError = (WorkflowRunErrorEvent)runEventHub.PublishedEvents[0].evt;
        backpressureError.Code.Should().Be(WorkflowProjectionSinkFailurePolicy.SinkBackpressureErrorCode);

        runEventHub.PublishedEvents.Clear();
        var handledCompleted = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new WorkflowRunEventSinkCompletedException());

        handledCompleted.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(2);
        runEventHub.PublishedEvents.Should().BeEmpty();

        var handledUnknown = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new ApplicationException("unknown"));

        handledUnknown.Should().BeFalse();
        sinkManager.DetachCalls.Should().Be(2);
    }

    [Fact]
    public async Task ReleaseService_WhenNoLiveSink_ShouldStopMarkAndRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var sinkManager = new RecordingSinkSubscriptionManager();
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            sinkManager,
            readModelUpdater,
            new WorkflowProjectionLeaseManager(ownership));
        var lease = CreateLease("actor-release", "cmd-release");

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().ContainSingle();
        readModelUpdater.MarkStoppedActorIds.Should().ContainSingle().Which.Should().Be("actor-release");
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-release", "cmd-release"));
    }

    [Fact]
    public async Task ReleaseService_WhenLiveSinkExists_ShouldSkipStopAndRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var sinkManager = new RecordingSinkSubscriptionManager { SubscriptionCount = 1 };
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            sinkManager,
            readModelUpdater,
            new WorkflowProjectionLeaseManager(ownership));
        var lease = CreateLease("actor-busy", "cmd-busy");

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().BeEmpty();
        readModelUpdater.MarkStoppedActorIds.Should().BeEmpty();
        ownership.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveSinkForwarder_WhenSinkThrowsAndPolicyHandles_ShouldNotThrow()
    {
        var policy = new RecordingSinkFailurePolicy
        {
            NextHandledResult = true,
        };
        var forwarder = new WorkflowProjectionLiveSinkForwarder(policy);
        var sink = new ThrowingRunEventSink(new InvalidOperationException("sink failed"));
        var sourceEvent = new WorkflowRunStartedEvent { ThreadId = "thread-1" };

        var act = async () => await forwarder.ForwardAsync(
            CreateLease("actor-forward", "cmd-forward"),
            sink,
            sourceEvent,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        policy.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task LiveSinkForwarder_WhenPolicyDoesNotHandle_ShouldRethrowOriginalError()
    {
        var policy = new RecordingSinkFailurePolicy
        {
            NextHandledResult = false,
        };
        var forwarder = new WorkflowProjectionLiveSinkForwarder(policy);
        var sink = new ThrowingRunEventSink(new InvalidOperationException("sink failed"));
        var sourceEvent = new WorkflowRunStartedEvent { ThreadId = "thread-1" };

        var act = async () => await forwarder.ForwardAsync(
            CreateLease("actor-forward-2", "cmd-forward-2"),
            sink,
            sourceEvent,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sink failed");
        policy.Calls.Should().ContainSingle();
    }

    private static InMemoryProjectionReadModelStore<WorkflowExecutionReport, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        listSortSelector: report => report.StartedAt);

    private static WorkflowExecutionRuntimeLease CreateLease(string actorId, string commandId) => new(
        new WorkflowExecutionProjectionContext
        {
            ProjectionId = actorId,
            RootActorId = actorId,
            CommandId = commandId,
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        });

    private sealed class TrackingOwnershipCoordinator : IProjectionOwnershipCoordinator
    {
        public List<(string ScopeId, string SessionId)> Acquired { get; } = [];
        public List<(string ScopeId, string SessionId)> Released { get; } = [];

        public Task AcquireAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Acquired.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Released.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingSinkSubscriptionManager : IWorkflowProjectionSinkSubscriptionManager
    {
        public int SubscriptionCount { get; set; }
        public int DetachCalls { get; private set; }

        public Task AttachOrReplaceAsync(
            WorkflowExecutionRuntimeLease lease,
            IWorkflowRunEventSink sink,
            Func<WorkflowRunEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachAsync(
            WorkflowExecutionRuntimeLease lease,
            IWorkflowRunEventSink sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            return Task.CompletedTask;
        }

        public int GetSubscriptionCount(WorkflowExecutionRuntimeLease lease)
        {
            _ = lease;
            return SubscriptionCount;
        }
    }

    private sealed class RecordingReadModelUpdater : IWorkflowProjectionReadModelUpdater
    {
        public List<(string ActorId, WorkflowExecutionProjectionContext Context)> Refreshed { get; } = [];
        public List<string> MarkStoppedActorIds { get; } = [];

        public Task RefreshMetadataAsync(
            string actorId,
            WorkflowExecutionProjectionContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Refreshed.Add((actorId, context));
            return Task.CompletedTask;
        }

        public Task MarkStoppedAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            MarkStoppedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public List<WorkflowExecutionProjectionContext> StartedContexts { get; } = [];
        public List<WorkflowExecutionProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default)
        {
            _ = context;
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        private readonly Exception _startException;

        public ThrowingLifecycleService(Exception startException)
        {
            _startException = startException;
        }

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromException(_startException);
        }

        public Task ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default)
        {
            _ = context;
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSinkFailurePolicy : IWorkflowProjectionSinkFailurePolicy
    {
        public bool NextHandledResult { get; set; }
        public List<(WorkflowExecutionRuntimeLease Lease, IWorkflowRunEventSink Sink, WorkflowRunEvent Event, Exception Exception)> Calls { get; } = [];

        public ValueTask<bool> TryHandleAsync(
            WorkflowExecutionRuntimeLease runtimeLease,
            IWorkflowRunEventSink sink,
            WorkflowRunEvent sourceEvent,
            Exception exception,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((runtimeLease, sink, sourceEvent, exception));
            return ValueTask.FromResult(NextHandledResult);
        }
    }

    private sealed class ThrowingRunEventSink : IWorkflowRunEventSink
    {
        private readonly Exception _exception;

        public ThrowingRunEventSink(Exception exception)
        {
            _exception = exception;
        }

        public void Push(WorkflowRunEvent evt)
        {
            _ = evt;
            throw _exception;
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            throw _exception;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEvent>
    {
        public List<(string scopeId, string sessionId, WorkflowRunEvent evt)> PublishedEvents { get; } = [];
        public List<TrackingSubscription> Subscriptions { get; } = [];

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowRunEvent evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishedEvents.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            var subscription = new TrackingSubscription();
            Subscriptions.Add(subscription);
            return Task.FromResult<IAsyncDisposable>(subscription);
        }
    }

    private sealed class TrackingSubscription : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopRunEventSink : IWorkflowRunEventSink
    {
        public void Push(WorkflowRunEvent evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
