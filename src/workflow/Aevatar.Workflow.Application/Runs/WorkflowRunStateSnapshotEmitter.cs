using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunStateSnapshotEmitter : IWorkflowRunStateSnapshotEmitter
{
    private readonly IWorkflowExecutionProjectionQueryPort _projectionQueryPort;
    private readonly IWorkflowRunOutputStreamer _outputStreamer;

    public WorkflowRunStateSnapshotEmitter(
        IWorkflowExecutionProjectionQueryPort projectionQueryPort,
        IWorkflowRunOutputStreamer outputStreamer)
    {
        _projectionQueryPort = projectionQueryPort;
        _outputStreamer = outputStreamer;
    }

    public async Task EmitAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        WorkflowProjectionCompletionStatus projectionCompletionStatus,
        bool projectionCompleted,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var snapshot = await TryGetSnapshotAsync(receipt.ActorId, ct);
        var snapshotPayload = new WorkflowProjectionStateSnapshotPayload
        {
            ActorId = receipt.ActorId,
            WorkflowName = receipt.WorkflowName,
            CommandId = receipt.CommandId,
            ProjectionCompleted = projectionCompleted,
            ProjectionCompletionStatus = MapStatus(projectionCompletionStatus),
            SnapshotAvailable = snapshot != null,
            Snapshot = snapshot == null ? null : MapSnapshot(snapshot),
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
            return await _projectionQueryPort.GetActorSnapshotAsync(actorId, ct);
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
}
