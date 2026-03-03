namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowChatRunRequest(
    string Prompt,
    string? WorkflowName,
    string? ActorId,
    // Inline workflow YAML bundle; first item is the entry workflow.
    IReadOnlyList<string>? WorkflowYamls = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public enum WorkflowChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
    AgentTypeNotSupported = 3,
    ProjectionDisabled = 4,
    WorkflowBindingMismatch = 5,
    AgentWorkflowNotConfigured = 6,
    InvalidWorkflowYaml = 7,
    WorkflowNameMismatch = 8,
}

public enum WorkflowProjectionCompletionStatus
{
    Completed = 0,
    TimedOut = 1,
    Failed = 2,
    Stopped = 3,
    NotFound = 4,
    Disabled = 5,
    Unknown = 99,
}

public sealed record WorkflowChatRunStarted(
    string ActorId,
    string WorkflowName,
    string CommandId);

public sealed record WorkflowChatRunFinalizeResult(
    WorkflowProjectionCompletionStatus ProjectionCompletionStatus,
    bool ProjectionCompleted);

public sealed record WorkflowChatRunExecutionResult(
    WorkflowChatRunStartError Error,
    WorkflowChatRunStarted? Started,
    WorkflowChatRunFinalizeResult? FinalizeResult)
{
    public bool Succeeded => Error == WorkflowChatRunStartError.None;
}

public sealed record WorkflowStateSnapshotPayload
{
    public required string ActorId { get; init; }
    public required string WorkflowName { get; init; }
    public required string CommandId { get; init; }
    public required string ProjectionCompletionStatus { get; init; }
    public required bool ProjectionCompleted { get; init; }
    public required bool SnapshotAvailable { get; init; }
    public object? Snapshot { get; init; }
}

public sealed record WorkflowOutputFrame
{
    public required string Type { get; init; }
    public long? Timestamp { get; init; }
    public string? ThreadId { get; init; }
    public object? Result { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public string? StepName { get; init; }
    public string? MessageId { get; init; }
    public string? Role { get; init; }
    public string? Delta { get; init; }
    public object? Snapshot { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? Name { get; init; }
    public object? Value { get; init; }
}
