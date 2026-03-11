using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunDurableCompletionResolver
    : ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>
{
    private readonly IWorkflowExecutionProjectionQueryPort _projectionQueryPort;

    public WorkflowRunDurableCompletionResolver(
        IWorkflowExecutionProjectionQueryPort projectionQueryPort)
    {
        _projectionQueryPort = projectionQueryPort ?? throw new ArgumentNullException(nameof(projectionQueryPort));
    }

    public async Task<CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>> ResolveAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var actorId = receipt.ActorId;
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        try
        {
            var snapshot = await _projectionQueryPort.GetActorSnapshotAsync(actorId, ct);
            return MapSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete;
        }
    }

    private static CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus> MapSnapshot(WorkflowActorSnapshot? snapshot) =>
        snapshot?.CompletionStatus switch
        {
            WorkflowRunCompletionStatus.Completed => new(true, WorkflowProjectionCompletionStatus.Completed),
            WorkflowRunCompletionStatus.TimedOut => new(true, WorkflowProjectionCompletionStatus.TimedOut),
            WorkflowRunCompletionStatus.Failed => new(true, WorkflowProjectionCompletionStatus.Failed),
            WorkflowRunCompletionStatus.Stopped => new(true, WorkflowProjectionCompletionStatus.Stopped),
            WorkflowRunCompletionStatus.NotFound => new(true, WorkflowProjectionCompletionStatus.NotFound),
            WorkflowRunCompletionStatus.Disabled => new(true, WorkflowProjectionCompletionStatus.Disabled),
            _ => CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete,
        };
}
