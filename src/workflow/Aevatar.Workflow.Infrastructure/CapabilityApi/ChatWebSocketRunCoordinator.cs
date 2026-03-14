using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        ApiRequestScope scope,
        CancellationToken ct = default,
        WorkflowCapabilitiesDocument? capabilities = null,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
        var correlationId = string.Empty;
        CapabilityMessageTraceContext ResolveContext() =>
            CapabilityTraceContext.CreateMessageContext(correlationId, command.RequestId);

        var normalizedRequest = ChatRunRequestNormalizer.Normalize(command.Input, capabilities, defaultMetadata);
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

        if (!executionResult.Succeeded || executionResult.Receipt == null)
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
