using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public interface IWorkflowChatRunInteractionPort
{
    Task<WorkflowChatRunInteractionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}

public sealed record WorkflowChatRunStartFailureDetail(
    WorkflowChatRunStartError Error,
    string Message,
    ExternalCapabilityReadiness? ExternalCapabilityReadiness = null)
{
    public static WorkflowChatRunStartFailureDetail Create(
        WorkflowChatRunStartError error,
        string? message = null,
        ExternalCapabilityReadiness? externalCapabilityReadiness = null) =>
        new(
            error,
            string.IsNullOrWhiteSpace(message) ? DefaultMessage(error) : message,
            externalCapabilityReadiness?.Clone());

    private static string DefaultMessage(WorkflowChatRunStartError error) =>
        error == WorkflowChatRunStartError.InvalidWorkflowYaml
            ? "Workflow YAML is invalid."
            : string.Empty;
}

public sealed record WorkflowChatRunInteractionResult
{
    public required bool Succeeded { get; init; }
    public required WorkflowChatRunStartError Error { get; init; }
    public WorkflowChatInteractionAcceptedReceipt? Receipt { get; init; }
    public WorkflowProjectionCompletionStatus? Completion { get; init; }
    public bool Completed { get; init; }
    public CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>? FinalizeResult { get; init; }
    public WorkflowChatRunStartFailureDetail? FailureDetail { get; init; }

    public static WorkflowChatRunInteractionResult Success(
        WorkflowChatInteractionAcceptedReceipt receipt,
        CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus> finalizeResult)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(finalizeResult);

        return new WorkflowChatRunInteractionResult
        {
            Succeeded = true,
            Error = WorkflowChatRunStartError.None,
            Receipt = receipt,
            Completion = finalizeResult.Completion,
            Completed = finalizeResult.Completed,
            FinalizeResult = finalizeResult,
            FailureDetail = null,
        };
    }

    public static WorkflowChatRunInteractionResult Failure(
        WorkflowChatRunStartError error,
        WorkflowChatRunStartFailureDetail? failureDetail = null)
    {
        return new WorkflowChatRunInteractionResult
        {
            Succeeded = false,
            Error = error,
            Receipt = null,
            Completion = default,
            Completed = false,
            FinalizeResult = null,
            FailureDetail = failureDetail,
        };
    }
}
