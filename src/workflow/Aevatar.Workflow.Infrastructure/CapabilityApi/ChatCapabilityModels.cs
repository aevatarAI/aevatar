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
}

internal sealed record ChatWsCommand
{
    public string Type { get; init; } = ChatCapabilityMessageTypes.ChatCommand;
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
