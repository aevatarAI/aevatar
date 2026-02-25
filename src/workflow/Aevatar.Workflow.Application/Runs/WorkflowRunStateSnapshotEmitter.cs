using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

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
        WorkflowRunContext runContext,
        WorkflowProjectionCompletionStatus projectionCompletionStatus,
        bool projectionCompleted,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runContext);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var snapshot = await TryGetSnapshotAsync(runContext.ActorId, ct);
        var snapshotPayload = snapshot ?? new
        {
            actorId = runContext.ActorId,
            workflowName = runContext.WorkflowName,
            commandId = runContext.CommandId,
            projectionCompleted,
            projectionCompletionStatus = projectionCompletionStatus.ToString(),
            unavailable = true,
        };

        await emitAsync(
            _outputStreamer.Map(new WorkflowStateSnapshotEvent
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Snapshot = snapshotPayload,
            }),
            ct);
    }

    private async Task<object?> TryGetSnapshotAsync(string actorId, CancellationToken ct)
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
}
