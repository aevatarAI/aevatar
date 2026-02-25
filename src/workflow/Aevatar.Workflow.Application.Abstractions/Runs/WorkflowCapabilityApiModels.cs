namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record ChatInput
{
    public required string Prompt { get; init; }
    public string? Workflow { get; init; }
    public string? AgentId { get; init; }
}

public sealed record ChatWsCommand
{
    public string Type { get; init; } = WorkflowCapabilityMessageTypes.ChatCommand;
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
