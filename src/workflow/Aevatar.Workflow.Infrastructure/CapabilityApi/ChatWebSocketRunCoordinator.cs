using System.Net.WebSockets;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        IWorkflowChatRunInteractionPort chatRunService,
        ApiRequestScope scope,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        IFileArtifactIngressPort? fileIngressPort = null,
        string? trustedScopeId = null)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
        var correlationId = string.Empty;
        CapabilityMessageTraceContext ResolveContext() =>
            CapabilityTraceContext.CreateMessageContext(correlationId, command.RequestId);

        var normalizedRequest = await ChatRunRequestNormalizer.NormalizeAsync(
            command.Input,
            fileIngressPort,
            defaultMetadata,
            cancellationToken: ct,
            trustedScopeId: trustedScopeId);
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
            var (code, message) = executionResult.FailureDetail == null
                ? ChatRunStartErrorMapper.ToCommandError(executionResult.Error)
                : ChatRunStartErrorMapper.ToCommandError(executionResult.FailureDetail);
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
            correlationId = executionResult.Receipt.Run.CorrelationId;
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

        async ValueTask SendAckAndRecordAsync(WorkflowChatInteractionAcceptedReceipt receipt, CancellationToken token)
        {
            correlationId = receipt.Run.CorrelationId;
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, receipt.Run),
                token,
                responseMessageType);
            scope.RecordFirstResponse();
        }
    }
}
