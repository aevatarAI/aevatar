using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.RunForks;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandDispatchService
    : ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
{
    private readonly ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> _pipeline;

    public WorkflowForkRunCommandDispatchService(
        ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public async Task<CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>> DispatchAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var dispatch = await _pipeline.DispatchAsync(command, ct).ConfigureAwait(false);
            if (!dispatch.Succeeded || dispatch.Target == null)
                return CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Failure(dispatch.Error);

            return CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Success(
                dispatch.Target.Receipt,
                dispatch.Target.Admission);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.DispatchFailed(
                    Normalize(command.SourceRunId),
                    Normalize(command.StartAtStepId),
                    ex.Message));
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
