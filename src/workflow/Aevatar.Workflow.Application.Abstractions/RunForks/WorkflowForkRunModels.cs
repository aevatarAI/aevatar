namespace Aevatar.Workflow.Application.Abstractions.RunForks;

public sealed record WorkflowForkRunCommand(
    string SourceRunId,
    string StartAtStepId,
    string? InlineYaml = null,
    IReadOnlyDictionary<string, string>? InlineSubYamls = null,
    IReadOnlyDictionary<string, string>? VariableOverrides = null,
    string? Input = null,
    string? CommandId = null,
    string? CorrelationId = null);

public enum WorkflowForkRunStartErrorCode
{
    None = 0,
    SourceRunNotFound = 1,
    SourceRunNotTerminal = 2,
    InvalidWorkflowYaml = 3,
    StartStepNotFound = 4,
    RunCreationFailed = 5,
    DispatchFailed = 6,
}

public sealed record WorkflowForkRunStartError(
    WorkflowForkRunStartErrorCode Code,
    string SourceRunId,
    string StartAtStepId,
    string Reason)
{
    public static WorkflowForkRunStartError SourceRunNotFound(string sourceRunId) =>
        new(
            WorkflowForkRunStartErrorCode.SourceRunNotFound,
            sourceRunId ?? string.Empty,
            string.Empty,
            "Source workflow run was not found.");

    public static WorkflowForkRunStartError SourceRunNotTerminal(
        string sourceRunId,
        string status) =>
        new(
            WorkflowForkRunStartErrorCode.SourceRunNotTerminal,
            sourceRunId ?? string.Empty,
            string.Empty,
            string.IsNullOrWhiteSpace(status)
                ? "Source workflow run status is unknown."
                : $"Source workflow run status '{status}' is not terminal.");

    public static WorkflowForkRunStartError InvalidWorkflowYaml(
        string sourceRunId,
        string startAtStepId,
        string reason) =>
        new(
            WorkflowForkRunStartErrorCode.InvalidWorkflowYaml,
            sourceRunId ?? string.Empty,
            startAtStepId ?? string.Empty,
            string.IsNullOrWhiteSpace(reason)
                ? "Workflow YAML is invalid."
                : reason);

    public static WorkflowForkRunStartError StartStepNotFound(
        string sourceRunId,
        string startAtStepId) =>
        new(
            WorkflowForkRunStartErrorCode.StartStepNotFound,
            sourceRunId ?? string.Empty,
            startAtStepId ?? string.Empty,
            $"Start step '{startAtStepId}' was not found in workflow YAML.");

    public static WorkflowForkRunStartError RunCreationFailed(
        string sourceRunId,
        string startAtStepId,
        string reason) =>
        new(
            WorkflowForkRunStartErrorCode.RunCreationFailed,
            sourceRunId ?? string.Empty,
            startAtStepId ?? string.Empty,
            string.IsNullOrWhiteSpace(reason)
                ? "Workflow run creation failed."
                : reason);

    public static WorkflowForkRunStartError DispatchFailed(
        string sourceRunId,
        string startAtStepId,
        string reason) =>
        new(
            WorkflowForkRunStartErrorCode.DispatchFailed,
            sourceRunId ?? string.Empty,
            startAtStepId ?? string.Empty,
            string.IsNullOrWhiteSpace(reason)
                ? "Workflow fork dispatch failed."
                : reason);
}

public sealed record WorkflowForkRunAcceptedReceipt(
    string SourceRunId,
    string NewRunActorId,
    string WorkflowName,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt);

public sealed record WorkflowForkRunResult(
    bool Succeeded,
    WorkflowForkRunAcceptedReceipt? Receipt,
    WorkflowForkRunStartError? Error)
{
    public static WorkflowForkRunResult Accepted(WorkflowForkRunAcceptedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new WorkflowForkRunResult(true, receipt, null);
    }

    public static WorkflowForkRunResult Failure(WorkflowForkRunStartError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new WorkflowForkRunResult(false, null, error);
    }
}

public interface IWorkflowForkRunService
{
    Task<WorkflowForkRunResult> ForkAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default);
}
