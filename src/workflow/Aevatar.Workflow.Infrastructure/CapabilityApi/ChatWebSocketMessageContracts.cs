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
    public required string ActorId { get; init; }
    public required string Workflow { get; init; }
}

internal sealed record ChatWebSocketCommandAckEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.CommandAck;
    public required string RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
    public required ChatWebSocketCommandAckPayload Payload { get; init; }
}

internal sealed record ChatWebSocketRunEventEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.AguiEvent;
    public required string RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
    public required WorkflowOutputFrame Payload { get; init; }
}

internal sealed record ChatWebSocketCommandErrorEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.CommandError;
    public string? RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
    public required string Code { get; init; }
    public required string Message { get; init; }
}

internal static class ChatWebSocketEnvelopeFactory
{
    public static ChatWebSocketCommandAckEnvelope CreateCommandAck(
        string requestId,
        WorkflowChatRunStarted started,
        string traceId)
    {
        ArgumentNullException.ThrowIfNull(started);
        return new ChatWebSocketCommandAckEnvelope
        {
            RequestId = requestId,
            CorrelationId = started.CommandId,
            TraceId = traceId,
            Payload = new ChatWebSocketCommandAckPayload
            {
                CommandId = started.CommandId,
                ActorId = started.ActorId,
                Workflow = started.WorkflowName,
            },
        };
    }

    public static ChatWebSocketRunEventEnvelope CreateAguiEvent(
        string requestId,
        WorkflowOutputFrame payload,
        string correlationId,
        string traceId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new ChatWebSocketRunEventEnvelope
        {
            RequestId = requestId,
            CorrelationId = correlationId,
            TraceId = traceId,
            Payload = payload,
        };
    }

    public static ChatWebSocketCommandErrorEnvelope CreateCommandError(
        string? requestId,
        string code,
        string message,
        string correlationId = "",
        string traceId = "")
    {
        return new ChatWebSocketCommandErrorEnvelope
        {
            RequestId = requestId,
            CorrelationId = correlationId,
            TraceId = traceId,
            Code = code,
            Message = message,
        };
    }
}
