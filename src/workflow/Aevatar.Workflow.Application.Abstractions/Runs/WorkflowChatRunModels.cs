using Aevatar.Workflow.Application.Abstractions.Orchestration;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatRunRequest(
    string Prompt,
    string? WorkflowName,
    string? ActorId);

public enum WorkflowChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
    AgentTypeNotSupported = 3,
    ProjectionDisabled = 4,
}

public sealed record WorkflowChatRunPreparationResult(
    WorkflowChatRunStartError Error,
    WorkflowChatRunExecution? Execution);

public sealed record WorkflowChatRunExecution(
    string ActorId,
    string WorkflowName,
    string RunId,
    WorkflowProjectionRun ProjectionRun,
    Task ProcessingTask);
