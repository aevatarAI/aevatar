using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal interface IWorkflowRunDurableCompletionResolver
{
    Task<WorkflowRunDurableCompletionObservation> ResolveAsync(
        string actorId,
        CancellationToken ct = default);
}

internal readonly record struct WorkflowRunDurableCompletionObservation(
    bool HasTerminalStatus,
    WorkflowProjectionCompletionStatus Status)
{
    public static WorkflowRunDurableCompletionObservation Incomplete { get; } =
        new(false, WorkflowProjectionCompletionStatus.Unknown);
}

internal sealed class WorkflowRunDurableCompletionResolver
    : IWorkflowRunDurableCompletionResolver
{
    private readonly IWorkflowExecutionProjectionQueryPort _projectionQueryPort;

    public WorkflowRunDurableCompletionResolver(
        IWorkflowExecutionProjectionQueryPort projectionQueryPort)
    {
        _projectionQueryPort = projectionQueryPort ?? throw new ArgumentNullException(nameof(projectionQueryPort));
    }

    public async Task<WorkflowRunDurableCompletionObservation> ResolveAsync(
        string actorId,
        CancellationToken ct = default)
    {
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
            return WorkflowRunDurableCompletionObservation.Incomplete;
        }
    }

    private static WorkflowRunDurableCompletionObservation MapSnapshot(WorkflowActorSnapshot? snapshot) =>
        snapshot?.CompletionStatus switch
        {
            WorkflowRunCompletionStatus.Completed => new(true, WorkflowProjectionCompletionStatus.Completed),
            WorkflowRunCompletionStatus.TimedOut => new(true, WorkflowProjectionCompletionStatus.TimedOut),
            WorkflowRunCompletionStatus.Failed => new(true, WorkflowProjectionCompletionStatus.Failed),
            WorkflowRunCompletionStatus.Stopped => new(true, WorkflowProjectionCompletionStatus.Stopped),
            WorkflowRunCompletionStatus.NotFound => new(true, WorkflowProjectionCompletionStatus.NotFound),
            WorkflowRunCompletionStatus.Disabled => new(true, WorkflowProjectionCompletionStatus.Disabled),
            _ => WorkflowRunDurableCompletionObservation.Incomplete,
        };
}
