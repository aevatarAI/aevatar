using System.Text.Json;
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
    public required ChatWebSocketCommandAckPayload Payload { get; init; }
}

internal sealed record ChatWebSocketRunEventEnvelope
{
    public string Type { get; init; } = ChatWebSocketMessageTypes.AguiEvent;
    public required string RequestId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public required JsonElement Payload { get; init; }
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
        WorkflowChatRunAcceptedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new ChatWebSocketCommandAckEnvelope
        {
            RequestId = requestId,
            CorrelationId = receipt.CorrelationId,
            Payload = new ChatWebSocketCommandAckPayload
            {
                CommandId = receipt.CommandId,
                ActorId = receipt.ActorId,
                Workflow = receipt.WorkflowName,
            },
        };
    }

    public static ChatWebSocketRunEventEnvelope CreateAguiEvent(
        string requestId,
        JsonElement payload,
        string correlationId)
    {
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
