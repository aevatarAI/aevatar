namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatCapabilityMessageTypes
{
    public const string ChatCommand = "chat.command";
}

public sealed record ChatInput
{
    public required string Prompt { get; init; }
    public string? Workflow { get; init; }
    public string? AgentId { get; init; }
    public string? WorkflowYaml { get; init; }
}

public sealed record WorkflowResumeInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public string? CommandId { get; init; }
    public bool Approved { get; init; }
    public string? UserInput { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

public sealed record WorkflowSignalInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string SignalName { get; init; }
    public string? CommandId { get; init; }
    public string? Payload { get; init; }
}

internal sealed record ChatWsCommand
{
    public string Type { get; init; } = ChatCapabilityMessageTypes.ChatCommand;
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
