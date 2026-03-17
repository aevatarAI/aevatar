using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowRunControlStartErrorCode
{
    None = 0,
    InvalidActorId = 1,
    InvalidRunId = 2,
    ActorNotFound = 3,
    ActorNotWorkflowRun = 4,
    RunBindingMissing = 5,
    RunBindingMismatch = 6,
    InvalidStepId = 7,
    InvalidSignalName = 8,
}

public sealed record WorkflowRunControlStartError(
    WorkflowRunControlStartErrorCode Code,
    string ActorId,
    string RequestedRunId,
    string BoundRunId,
    string RequestedStepId = "",
    string RequestedSignalName = "")
{
    public static WorkflowRunControlStartError InvalidActorId(string actorId) =>
        new(WorkflowRunControlStartErrorCode.InvalidActorId, actorId ?? string.Empty, string.Empty, string.Empty);

    public static WorkflowRunControlStartError InvalidRunId(string actorId, string runId) =>
        new(WorkflowRunControlStartErrorCode.InvalidRunId, actorId ?? string.Empty, runId ?? string.Empty, string.Empty);

    public static WorkflowRunControlStartError ActorNotFound(string actorId, string runId) =>
        new(WorkflowRunControlStartErrorCode.ActorNotFound, actorId ?? string.Empty, runId ?? string.Empty, string.Empty);

    public static WorkflowRunControlStartError ActorNotWorkflowRun(string actorId, string runId) =>
        new(WorkflowRunControlStartErrorCode.ActorNotWorkflowRun, actorId ?? string.Empty, runId ?? string.Empty, string.Empty);

    public static WorkflowRunControlStartError RunBindingMissing(string actorId, string runId) =>
        new(WorkflowRunControlStartErrorCode.RunBindingMissing, actorId ?? string.Empty, runId ?? string.Empty, string.Empty);

    public static WorkflowRunControlStartError RunBindingMismatch(string actorId, string runId, string boundRunId) =>
        new(WorkflowRunControlStartErrorCode.RunBindingMismatch, actorId ?? string.Empty, runId ?? string.Empty, boundRunId ?? string.Empty);

    public static WorkflowRunControlStartError InvalidStepId(string actorId, string runId, string stepId) =>
        new(
            WorkflowRunControlStartErrorCode.InvalidStepId,
            actorId ?? string.Empty,
            runId ?? string.Empty,
            string.Empty,
            stepId ?? string.Empty);

    public static WorkflowRunControlStartError InvalidSignalName(string actorId, string runId, string signalName) =>
        new(
            WorkflowRunControlStartErrorCode.InvalidSignalName,
            actorId ?? string.Empty,
            runId ?? string.Empty,
            string.Empty,
            RequestedSignalName: signalName ?? string.Empty);
}

public interface IWorkflowRunControlCommand : ICommandContextSeed
{
    string ActorId { get; }

    string RunId { get; }
}

public abstract record WorkflowRunControlCommandBase(
    string ActorId,
    string RunId,
    string? CommandId) : IWorkflowRunControlCommand
{
    public string? CorrelationId => string.IsNullOrWhiteSpace(CommandId) ? null : CommandId;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record WorkflowResumeCommand(
    string ActorId,
    string RunId,
    string StepId,
    string? CommandId,
    bool Approved,
    string? UserInput,
    IReadOnlyDictionary<string, string>? Metadata = null) : WorkflowRunControlCommandBase(ActorId, RunId, CommandId);

public sealed record WorkflowSignalCommand(
    string ActorId,
    string RunId,
    string SignalName,
    string? CommandId,
    string? Payload,
    string? StepId = null) : WorkflowRunControlCommandBase(ActorId, RunId, CommandId);

public sealed record WorkflowStopCommand(
    string ActorId,
    string RunId,
    string? CommandId,
    string? Reason = null) : WorkflowRunControlCommandBase(ActorId, RunId, CommandId);

public sealed record WorkflowRunControlAcceptedReceipt(
    string ActorId,
    string RunId,
    string CommandId,
    string CorrelationId);
