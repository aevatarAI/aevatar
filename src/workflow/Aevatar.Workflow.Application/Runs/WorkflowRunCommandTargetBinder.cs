using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetBinder
    : ICommandTargetBinder<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;
    private readonly IWorkflowRunActorPort _actorPort;

    public WorkflowRunCommandTargetBinder(
        IWorkflowExecutionProjectionLifecyclePort projectionPort,
        IWorkflowRunActorPort actorPort)
    {
        _projectionPort = projectionPort;
        _actorPort = actorPort;
    }

    public async Task<CommandTargetBindingResult<WorkflowChatRunStartError>> BindAsync(
        WorkflowChatRunRequest command,
        WorkflowRunCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<WorkflowRunEvent>();

        try
        {
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    target.WorkflowName,
                    command.Prompt,
                    context.CommandId,
                    token),
                sink,
                ct);

            if (projectionLease == null)
            {
                await sink.DisposeAsync();
                await ThrowIfRollbackFailedAsync(target.CreatedActorIds);
                return CommandTargetBindingResult<WorkflowChatRunStartError>.Failure(
                    WorkflowChatRunStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(projectionLease, sink);
            return CommandTargetBindingResult<WorkflowChatRunStartError>.Success();
        }
        catch (Exception ex)
        {
            await sink.DisposeAsync();

            var rollbackError = await TryRollbackCreatedActorsAsync(target.CreatedActorIds);
            if (rollbackError == null)
                throw;

            ExceptionDispatchInfo.Capture(
                new AggregateException(
                    "Workflow run target binding failed and rollback also failed.",
                    ex,
                    rollbackError)).Throw();
            throw;
        }
    }

    private async Task ThrowIfRollbackFailedAsync(IReadOnlyList<string>? actorIds)
    {
        var rollbackError = await TryRollbackCreatedActorsAsync(actorIds);
        if (rollbackError != null)
            throw rollbackError;
    }

    private async Task<Exception?> TryRollbackCreatedActorsAsync(IReadOnlyList<string>? actorIds)
    {
        if (actorIds == null || actorIds.Count == 0)
            return null;

        List<Exception>? failures = null;
        foreach (var actorId in actorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _actorPort.DestroyAsync(actorId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException(
                    $"Failed to rollback workflow actor '{actorId}'.",
                    ex));
            }
        }

        return failures switch
        {
            null => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException("Workflow actor rollback failed.", failures),
        };
    }
}
