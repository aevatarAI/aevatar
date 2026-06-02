using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunObservationScopeLeasePreparation
    : ICommandObservationScopeLeasePreparation<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    private readonly IWorkflowChatRunObservationScopeLeasePreparationPort _port;

    public WorkflowRunObservationScopeLeasePreparation(
        IWorkflowChatRunObservationScopeLeasePreparationPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    public async Task<CommandObservationScopeLeasePreparationResult<WorkflowChatRunStartError>> PrepareAsync(
        WorkflowChatRunRequest command,
        CommandDispatchExecution<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var preparation = await _port.PrepareAsync(
            execution.Target.ActorId,
            execution.Context.CommandId,
            ct).ConfigureAwait(false);
        if (preparation == null)
            return CommandObservationScopeLeasePreparationResult<WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.ProjectionUnavailable);

        return CommandObservationScopeLeasePreparationResult<WorkflowChatRunStartError>.Success(
            new PreparedObservationScopeHandle(_port, preparation));
    }

    private sealed class PreparedObservationScopeHandle(
        IWorkflowChatRunObservationScopeLeasePreparationPort port,
        WorkflowChatRunObservationScopeLeasePreparation preparation)
        : ICommandObservationScopeLeasePreparationHandle
    {
        public Task ReleaseAsync(CancellationToken ct = default) =>
            port.ReleaseAsync(preparation, ct);
    }
}
