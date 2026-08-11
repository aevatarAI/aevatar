using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandDispatchService
    : ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
{
    private readonly ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> _pipeline;
    private readonly IWorkflowRunLineageRecordingPort _lineageRecordingPort;

    public WorkflowForkRunCommandDispatchService(
        ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> pipeline,
        IWorkflowRunLineageRecordingPort lineageRecordingPort)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _lineageRecordingPort = lineageRecordingPort ?? throw new ArgumentNullException(nameof(lineageRecordingPort));
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

            await RecordAcceptedForkLineageAsync(command, dispatch.Target.Target, dispatch.Target.Receipt, ct)
                .ConfigureAwait(false);

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

    private async Task RecordAcceptedForkLineageAsync(
        WorkflowForkRunCommand command,
        WorkflowForkRunCommandTarget target,
        WorkflowForkRunAcceptedReceipt? receipt,
        CancellationToken ct)
    {
        if (receipt == null || !receipt.Accepted)
            return;

        var sourceRunId = Normalize(receipt.SourceRunId);
        var childRunId = Normalize(receipt.NewRunId);
        if (string.IsNullOrWhiteSpace(sourceRunId) || string.IsNullOrWhiteSpace(childRunId))
            return;

        await _lineageRecordingPort.RecordForkChildAsync(
            sourceRunId,
            childRunId,
            receipt.NewRunActorId ?? string.Empty,
            string.IsNullOrWhiteSpace(receipt.OriginalRunId) ? sourceRunId : receipt.OriginalRunId,
            target.StartAtStepId,
            Math.Max(0, command.Attempt),
            ct).ConfigureAwait(false);
    }
}
