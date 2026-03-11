using System.Net.WebSockets;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        IWorkflowRunInteractionService chatRunService,
        ApiRequestScope scope,
        CancellationToken ct = default)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
        var correlationId = string.Empty;
        CapabilityMessageTraceContext ResolveContext() =>
            CapabilityTraceContext.CreateMessageContext(correlationId, command.RequestId);

        var normalizedRequest = ChatRunRequestNormalizer.Normalize(command.Input);
        if (!normalizedRequest.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
            var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
            scope.MarkResult(statusCode);
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandError(
                    command.RequestId, code, message, context.CorrelationId),
                ct,
                responseMessageType);
            scope.RecordFirstResponse();
            return;
        }

        var executionResult = await chatRunService.ExecuteAsync(
            normalizedRequest.Request!,
            SendAguiEventAndRecordAsync,
            onAcceptedAsync: SendAckAndRecordAsync,
            ct);

        if (executionResult.Error != WorkflowChatRunStartError.None)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(executionResult.Error);
            var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(executionResult.Error);
            scope.MarkResult(statusCode);
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandError(
                    command.RequestId, code, message, context.CorrelationId),
                ct,
                responseMessageType);
            scope.RecordFirstResponse();
            return;
        }

        if (executionResult.Receipt != null)
            correlationId = executionResult.Receipt.CorrelationId;
        return;

        async ValueTask SendAguiEventAndRecordAsync(WorkflowRunEventEnvelope frame, CancellationToken token)
        {
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateAguiEvent(
                    command.RequestId, ChatJsonPayloads.ToJsonElement(frame), context.CorrelationId),
                token,
                responseMessageType);
            scope.RecordFirstResponse();
        }

        async ValueTask SendAckAndRecordAsync(WorkflowChatRunAcceptedReceipt receipt, CancellationToken token)
        {
            correlationId = receipt.CorrelationId;
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, receipt),
                token,
                responseMessageType);
            scope.RecordFirstResponse();
        }
    }
}
