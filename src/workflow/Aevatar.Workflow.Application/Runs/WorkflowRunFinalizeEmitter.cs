using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunFinalizeEmitter
    : ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>
{
    private readonly IWorkflowExecutionCurrentStateQueryPort _currentStateQueryPort;

    public WorkflowRunFinalizeEmitter(IWorkflowExecutionCurrentStateQueryPort currentStateQueryPort)
    {
        _currentStateQueryPort = currentStateQueryPort ?? throw new ArgumentNullException(nameof(currentStateQueryPort));
    }

    public async Task EmitAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        WorkflowProjectionCompletionStatus completion,
        bool completed,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var snapshot = await TryGetSnapshotAsync(receipt.ActorId, ct);
        var projectionState = await TryGetProjectionStateAsync(receipt.ActorId, ct);
        var snapshotPayload = new WorkflowProjectionStateSnapshotPayload
        {
            ActorId = receipt.ActorId,
            WorkflowName = receipt.WorkflowName,
            CommandId = receipt.CommandId,
            ProjectionCompleted = completed,
            ProjectionCompletionStatus = MapStatus(completion),
            SnapshotAvailable = snapshot != null,
            Snapshot = snapshot == null ? null : MapSnapshot(snapshot),
            ProjectionState = projectionState == null ? null : MapProjectionState(projectionState),
        };

        await emitAsync(
            new WorkflowRunEventEnvelope
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                StateSnapshot = new WorkflowStateSnapshotEventPayload
                {
                    Snapshot = Any.Pack(snapshotPayload),
                },
            },
            ct);
    }

    private async Task<WorkflowActorSnapshot?> TryGetSnapshotAsync(string actorId, CancellationToken ct)
    {
        try
        {
            return await _currentStateQueryPort.GetActorSnapshotAsync(actorId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<WorkflowActorProjectionState?> TryGetProjectionStateAsync(string actorId, CancellationToken ct)
    {
        try
        {
            return await _currentStateQueryPort.GetActorProjectionStateAsync(actorId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static WorkflowProjectionCompletionStatusPayload MapStatus(WorkflowProjectionCompletionStatus status) =>
        status switch
        {
            WorkflowProjectionCompletionStatus.Completed => WorkflowProjectionCompletionStatusPayload.Completed,
            WorkflowProjectionCompletionStatus.TimedOut => WorkflowProjectionCompletionStatusPayload.TimedOut,
            WorkflowProjectionCompletionStatus.Failed => WorkflowProjectionCompletionStatusPayload.Failed,
            WorkflowProjectionCompletionStatus.Stopped => WorkflowProjectionCompletionStatusPayload.Stopped,
            WorkflowProjectionCompletionStatus.NotFound => WorkflowProjectionCompletionStatusPayload.NotFound,
            WorkflowProjectionCompletionStatus.Disabled => WorkflowProjectionCompletionStatusPayload.Disabled,
            _ => WorkflowProjectionCompletionStatusPayload.Unknown,
        };

    private static WorkflowActorSnapshotPayload MapSnapshot(WorkflowActorSnapshot snapshot) =>
        new()
        {
            ActorId = snapshot.ActorId,
            WorkflowName = snapshot.WorkflowName,
            LastCommandId = snapshot.LastCommandId,
            StateVersion = snapshot.StateVersion,
            LastEventId = snapshot.LastEventId,
            LastUpdatedAtUtc = Timestamp.FromDateTimeOffset(snapshot.LastUpdatedAt.ToUniversalTime()),
            LastSuccess = snapshot.LastSuccess,
            LastOutput = snapshot.LastOutput,
            LastError = snapshot.LastError,
            TotalSteps = snapshot.TotalSteps,
            RequestedSteps = snapshot.RequestedSteps,
            CompletedSteps = snapshot.CompletedSteps,
            RoleReplyCount = snapshot.RoleReplyCount,
        };

    private static WorkflowActorProjectionStatePayload MapProjectionState(WorkflowActorProjectionState state) =>
        new()
        {
            ActorId = state.ActorId,
            LastCommandId = state.LastCommandId,
            StateVersion = state.StateVersion,
            LastEventId = state.LastEventId,
            LastUpdatedAtUtc = Timestamp.FromDateTimeOffset(state.LastUpdatedAt.ToUniversalTime()),
        };
}
