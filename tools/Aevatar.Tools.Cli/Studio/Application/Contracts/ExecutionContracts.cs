namespace Aevatar.Tools.Cli.Studio.Application.Contracts;

public sealed record StartExecutionRequest(
    string WorkflowName,
    string Prompt,
    IReadOnlyList<string> WorkflowYamls,
    string? RuntimeBaseUrl = null,
    string? ScopeId = null,
    string? WorkflowId = null,
    string? EventFormat = null);

public sealed record ResumeExecutionRequest(
    string RunId,
    string StepId,
    bool Approved,
    string? UserInput = null,
    string? SuspensionType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ExecutionSummary(
    string ExecutionId,
    string WorkflowName,
    string Status,
    string PromptPreview,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ActorId,
    string? Error);

public sealed record ExecutionFrameDto(
    DateTimeOffset ReceivedAtUtc,
    string Payload);

public sealed record ExecutionDetail(
    string ExecutionId,
    string WorkflowName,
    string Prompt,
    string RuntimeBaseUrl,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ActorId,
    string? Error,
    IReadOnlyList<ExecutionFrameDto> Frames);
