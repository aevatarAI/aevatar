using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketMessageTypes
{
    public const string CommandAck = "command.ack";
    public const string CommandError = "command.error";
    public const string AguiEvent = "agui.event";
}

internal sealed record ChatWebSocketCommandAckPayload
{
    public required string CommandId { get; init; }
    public required string RunActorId { get; init; }
    public string? DefinitionActorId { get; init; }
    public required string Workflow { get; init; }
}

internal sealed record ChatWebSocketCommandAckEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.CommandAck;
    public required string RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public required ChatWebSocketCommandAckPayload Payload { get; init; }
}

internal sealed record ChatWebSocketRunEventEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.AguiEvent;
    public required string RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public required WorkflowOutputFrame Payload { get; init; }
}

internal sealed record ChatWebSocketCommandErrorEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.CommandError;
    public string? RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public required string Code { get; init; }
    public required string Message { get; init; }
}

internal static class ChatWebSocketEnvelopeFactory
{
    public static ChatWebSocketCommandAckEnvelope CreateCommandAck(
        string requestId,
        WorkflowChatRunStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        return new ChatWebSocketCommandAckEnvelope
        {
            RequestId = requestId,
            CorrelationId = started.CommandId,
            Payload = new ChatWebSocketCommandAckPayload
            {
                CommandId = started.CommandId,
                RunActorId = started.RunActorId,
                DefinitionActorId = started.DefinitionActorId,
                Workflow = started.WorkflowName,
            },
        };
    }

    public static ChatWebSocketRunEventEnvelope CreateAguiEvent(
        string requestId,
        WorkflowOutputFrame payload,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new ChatWebSocketRunEventEnvelope
        {
            RequestId = requestId,
            CorrelationId = correlationId,
            Payload = payload,
        };
    }

    public static ChatWebSocketCommandErrorEnvelope CreateCommandError(
        string? requestId,
        string code,
        string message,
        string correlationId = "")
    {
        return new ChatWebSocketCommandErrorEnvelope
        {
            RequestId = requestId,
            CorrelationId = correlationId,
            Code = code,
            Message = message,
        };
    }
}
