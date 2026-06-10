using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Sdk.Contracts;

public sealed record ChatRunRequest
{
    public string? Prompt { get; init; }
    public IReadOnlyList<ChatRunContentPart>? InputParts { get; init; }
    public string? ScopeId { get; init; }
    public string? Workflow { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyList<string>? WorkflowYamls { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

public sealed record ChatRunContentPart
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public ChatRunInlineFilePart? InlineFile { get; init; }
    public ChatRunFileRefPart? FileRef { get; init; }
}

public sealed record ChatRunInlineFilePart
{
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Name { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed record ChatRunFileRefPart
{
    public string? FileId { get; init; }
    public string? ArtifactId { get; init; }
    public string? SourceKind { get; init; }
    public string? SourceMessageId { get; init; }
    public string? SourceResourceKey { get; init; }
    public string? FileName { get; init; }
    public string? Uri { get; init; }
    public string? MediaType { get; init; }
    public string? Name { get; init; }
    public long? CreatedAtUnixMs { get; init; }
    public long? ExpiresAtUnixMs { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record WorkflowResumeRequest
{
    public required string ScopeId { get; init; }
    public required string ServiceId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public string? ActorId { get; init; }
    public string? CommandId { get; init; }
    public bool Approved { get; init; }
    public string? UserInput { get; init; }
    public string? EditedContent { get; init; }
    public string? Feedback { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

public sealed record WorkflowSignalRequest
{
    public required string ScopeId { get; init; }
    public required string ServiceId { get; init; }
    public required string RunId { get; init; }
    public required string SignalName { get; init; }
    public string? ActorId { get; init; }
    public string? StepId { get; init; }
    public string? CommandId { get; init; }
    public string? Payload { get; init; }
}

public sealed record WorkflowResumeResponse
{
    public bool Accepted { get; init; }
    public string? ActorId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? CommandId { get; init; }
}

public sealed record WorkflowSignalResponse
{
    public bool Accepted { get; init; }
    public string? ActorId { get; init; }
    public string? RunId { get; init; }
    public string? SignalName { get; init; }
    public string? StepId { get; init; }
    public string? CommandId { get; init; }
}

public static class WorkflowEventTypes
{
    public const string RunStarted = WorkflowRunEventTypes.RunStarted;
    public const string RunFinished = WorkflowRunEventTypes.RunFinished;
    public const string RunError = WorkflowRunEventTypes.RunError;
    public const string RunStopped = WorkflowRunEventTypes.RunStopped;
    public const string StepStarted = WorkflowRunEventTypes.StepStarted;
    public const string StepFinished = WorkflowRunEventTypes.StepFinished;
    public const string TextMessageStart = WorkflowRunEventTypes.TextMessageStart;
    public const string TextMessageContent = WorkflowRunEventTypes.TextMessageContent;
    public const string TextMessageEnd = WorkflowRunEventTypes.TextMessageEnd;
    public const string StateSnapshot = WorkflowRunEventTypes.StateSnapshot;
    public const string ToolCallStart = WorkflowRunEventTypes.ToolCallStart;
    public const string ToolCallEnd = WorkflowRunEventTypes.ToolCallEnd;
    public const string Custom = WorkflowRunEventTypes.Custom;
}

public sealed record WorkflowEvent
{
    public required WorkflowRunEventEnvelope Frame { get; init; }

    public string Type => WorkflowRunEventTypes.GetEventType(Frame);

    public bool IsRunError =>
        Frame.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunError;

    public bool IsTerminal =>
        Frame.EventCase is WorkflowRunEventEnvelope.EventOneofCase.RunFinished
            or WorkflowRunEventEnvelope.EventOneofCase.RunError
            or WorkflowRunEventEnvelope.EventOneofCase.RunStopped;

    public static WorkflowEvent FromFrame(WorkflowRunEventEnvelope frame) =>
        new() { Frame = frame };
}

public sealed record WorkflowRunResult(IReadOnlyList<WorkflowEvent> Events)
{
    public WorkflowEvent? TerminalEvent => Events.LastOrDefault(x => x.IsTerminal);

    public WorkflowEvent? RunErrorEvent => Events.LastOrDefault(x => x.IsRunError);

    public bool Succeeded =>
        RunErrorEvent is null &&
        TerminalEvent?.Frame.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished;
}
