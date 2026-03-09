namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatCapabilityMessageTypes
{
    public const string ChatCommand = "chat.command";
}

public sealed record ChatInput
{
    /// <summary>User prompt for this chat run.</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Workflow identifier lookup (built-ins and file-loaded workflows).
    /// This field does not accept inline YAML semantics.
    /// </summary>
    public string? Workflow { get; init; }

    public string? AgentId { get; init; }

    /// <summary>
    /// Inline workflow YAML bundle for this request.
    /// The first item is treated as the entry workflow.
    /// If present, this field takes precedence over <see cref="Workflow"/>.
    /// </summary>
    public IReadOnlyList<string>? WorkflowYamls { get; init; }

    /// <summary>
    /// Optional run metadata passthrough for internal bridge integrations.
    /// </summary>
    public IDictionary<string, string>? Metadata { get; init; }
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
    public string? StepId { get; init; }
    public string? CommandId { get; init; }
    public string? Payload { get; init; }
}

internal sealed record ChatWsCommand
{
    public string Type { get; init; } = ChatCapabilityMessageTypes.ChatCommand;
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
