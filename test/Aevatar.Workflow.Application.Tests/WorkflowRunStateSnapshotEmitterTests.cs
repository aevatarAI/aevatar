using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunFinalizeEmitterTests
{
    public static TheoryData<WorkflowProjectionCompletionStatus, WorkflowProjectionCompletionStatusPayload> StatusMappings =>
        new()
        {
            { WorkflowProjectionCompletionStatus.Completed, WorkflowProjectionCompletionStatusPayload.Completed },
            { WorkflowProjectionCompletionStatus.TimedOut, WorkflowProjectionCompletionStatusPayload.TimedOut },
            { WorkflowProjectionCompletionStatus.Failed, WorkflowProjectionCompletionStatusPayload.Failed },
            { WorkflowProjectionCompletionStatus.Stopped, WorkflowProjectionCompletionStatusPayload.Stopped },
            { WorkflowProjectionCompletionStatus.NotFound, WorkflowProjectionCompletionStatusPayload.NotFound },
            { WorkflowProjectionCompletionStatus.Disabled, WorkflowProjectionCompletionStatusPayload.Disabled },
            { WorkflowProjectionCompletionStatus.Unknown, WorkflowProjectionCompletionStatusPayload.Unknown },
        };

    [Theory]
    [MemberData(nameof(StatusMappings))]
    public async Task EmitAsync_ShouldMapProjectionStatus(
        WorkflowProjectionCompletionStatus status,
        WorkflowProjectionCompletionStatusPayload expected)
    {
        var emitter = new WorkflowRunFinalizeEmitter(new FakeProjectionQueryPort());
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        WorkflowRunEventEnvelope? emitted = null;

        await emitter.EmitAsync(
            receipt,
            status,
            completed: true,
            (evt, _) =>
            {
                emitted = evt;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        emitted.Should().NotBeNull();
        emitted!.StateSnapshot.Should().NotBeNull();
        var payload = emitted.StateSnapshot.Snapshot.Unpack<WorkflowProjectionStateSnapshotPayload>();
        payload.ProjectionCompletionStatus.Should().Be(expected);
        payload.ProjectionCompleted.Should().BeTrue();
        payload.SnapshotAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task EmitAsync_ShouldIncludeMappedSnapshot_WhenProjectionSnapshotExists()
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = "actor-1",
            WorkflowName = "direct",
            LastSuccess = true,
            LastOutput = "ok",
            LastError = string.Empty,
            TotalSteps = 8,
            RequestedSteps = 7,
            CompletedSteps = 6,
            RoleReplyCount = 5,
        };
        var projectionState = new WorkflowActorProjectionState
        {
            ActorId = "actor-1",
            LastCommandId = "cmd-1",
            StateVersion = 42,
            LastEventId = "evt-1",
            LastUpdatedAt = new DateTimeOffset(2026, 3, 11, 9, 30, 0, TimeSpan.Zero),
        };
        var emitter = new WorkflowRunFinalizeEmitter(
            new FakeProjectionQueryPort
            {
                Snapshot = snapshot,
                ProjectionState = projectionState,
            });
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
        WorkflowProjectionStateSnapshotPayload? payload = null;

        await emitter.EmitAsync(
            receipt,
            WorkflowProjectionCompletionStatus.Completed,
            completed: false,
            (evt, _) =>
            {
                payload = evt.StateSnapshot.Snapshot.Unpack<WorkflowProjectionStateSnapshotPayload>();
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.ActorId.Should().Be("actor-1");
        payload.WorkflowName.Should().Be("direct");
        payload.CommandId.Should().Be("cmd-1");
        payload.ProjectionCompleted.Should().BeFalse();
        payload.SnapshotAvailable.Should().BeTrue();
        payload.Snapshot.Should().NotBeNull();
        payload.Snapshot.ActorId.Should().Be("actor-1");
        payload.Snapshot.LastSuccess.Should().BeTrue();
        payload.Snapshot.TotalSteps.Should().Be(8);
        payload.Snapshot.RoleReplyCount.Should().Be(5);
        payload.ProjectionState.Should().NotBeNull();
        payload.ProjectionState.LastCommandId.Should().Be("cmd-1");
        payload.ProjectionState.StateVersion.Should().Be(42);
    }

    [Fact]
    public async Task EmitAsync_ShouldSwallowSnapshotLookupFailures_AndEmitUnavailableSnapshot()
    {
        var emitter = new WorkflowRunFinalizeEmitter(
            new FakeProjectionQueryPort { SnapshotException = new InvalidOperationException("boom") });
        WorkflowProjectionStateSnapshotPayload? payload = null;

        await emitter.EmitAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1"),
            WorkflowProjectionCompletionStatus.Failed,
            completed: false,
            (evt, _) =>
            {
                payload = evt.StateSnapshot.Snapshot.Unpack<WorkflowProjectionStateSnapshotPayload>();
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.SnapshotAvailable.Should().BeFalse();
        payload.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task EmitAsync_ShouldRethrowCancellationFromSnapshotLookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var emitter = new WorkflowRunFinalizeEmitter(
            new FakeProjectionQueryPort { SnapshotException = new OperationCanceledException(cts.Token) });

        var act = async () => await emitter.EmitAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1"),
            WorkflowProjectionCompletionStatus.Completed,
            completed: true,
            static (_, _) => ValueTask.CompletedTask,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeProjectionQueryPort : IWorkflowExecutionProjectionQueryPort
    {
        public bool EnableActorQueryEndpoints => true;
        public WorkflowActorSnapshot? Snapshot { get; set; }
        public WorkflowActorProjectionState? ProjectionState { get; set; }
        public Exception? SnapshotException { get; set; }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            if (SnapshotException != null)
                throw SnapshotException;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            if (SnapshotException != null)
                throw SnapshotException;
            return Task.FromResult(ProjectionState);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(string actorId, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(string actorId, int depth = 2, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
