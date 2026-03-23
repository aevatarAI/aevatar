using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Sdk.Contracts;

public sealed record ChatRunRequest
{
    public string? Prompt { get; init; }
    public IReadOnlyList<ChatRunContentPart>? InputParts { get; init; }
    public string? Workflow { get; init; }
    public string? AgentId { get; init; }
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
}

public sealed record WorkflowResumeRequest
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public string? CommandId { get; init; }
    public bool Approved { get; init; }
    public string? UserInput { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

public sealed record WorkflowSignalRequest
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string SignalName { get; init; }
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

public sealed record WorkflowOutputFrame
{
    public required string Type { get; init; }
    public long? Timestamp { get; init; }
    public string? ThreadId { get; init; }
    public JsonElement? Result { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public string? StepName { get; init; }
    public string? MessageId { get; init; }
    public string? Role { get; init; }
    public string? Delta { get; init; }
    public JsonElement? Snapshot { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? Name { get; init; }
    public JsonElement? Value { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public static class WorkflowEventTypes
{
    public const string RunStarted = "RUN_STARTED";
    public const string RunFinished = "RUN_FINISHED";
    public const string RunError = "RUN_ERROR";
    public const string StepStarted = "STEP_STARTED";
    public const string StepFinished = "STEP_FINISHED";
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string StateSnapshot = "STATE_SNAPSHOT";
    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallEnd = "TOOL_CALL_END";
    public const string Custom = "CUSTOM";
}

public sealed record WorkflowEvent
{
    public required WorkflowOutputFrame Frame { get; init; }

    public string Type => Frame.Type;

    public bool IsRunError =>
        string.Equals(Type, WorkflowEventTypes.RunError, StringComparison.Ordinal);

    public bool IsTerminal =>
        string.Equals(Type, WorkflowEventTypes.RunFinished, StringComparison.Ordinal) ||
        string.Equals(Type, WorkflowEventTypes.RunError, StringComparison.Ordinal);

    public static WorkflowEvent FromFrame(WorkflowOutputFrame frame) =>
        new() { Frame = frame };
}

public sealed record WorkflowRunResult(IReadOnlyList<WorkflowEvent> Events)
{
    public WorkflowEvent? TerminalEvent => Events.LastOrDefault(x => x.IsTerminal);

    public WorkflowEvent? RunErrorEvent => Events.LastOrDefault(x => x.IsRunError);

    public bool Succeeded =>
        RunErrorEvent is null &&
        string.Equals(TerminalEvent?.Type, WorkflowEventTypes.RunFinished, StringComparison.Ordinal);
}
