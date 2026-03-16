namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowChatInputPartKind
{
    Unspecified = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
}

public sealed record WorkflowChatInputPart
{
    public required WorkflowChatInputPartKind Kind { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
}

public sealed record WorkflowChatRunRequest(
    string Prompt,
    string? WorkflowName,
    string? ActorId,
    string? SessionId = null,
    IReadOnlyList<WorkflowChatInputPart>? InputParts = null,
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
    DetachedCleanupUnavailable = 9,
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

public sealed record WorkflowChatRunAcceptedReceipt(
    string ActorId,
    string WorkflowName,
    string CommandId,
    string CorrelationId);
