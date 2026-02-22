using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Adapters;

public sealed class WorkflowCommandExecutionServiceAdapter
    : ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunCommandService _inner;

    public WorkflowCommandExecutionServiceAdapter(IWorkflowRunCommandService inner)
    {
        _inner = inner;
    }

    public async Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> ExecuteAsync(
        WorkflowChatRunRequest command,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        var result = await _inner.ExecuteAsync(command, emitAsync, onStartedAsync, ct);
        return new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
            result.Error,
            result.Started,
            result.FinalizeResult);
    }
}
