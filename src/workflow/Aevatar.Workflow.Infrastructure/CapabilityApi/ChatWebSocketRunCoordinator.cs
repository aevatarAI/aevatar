using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
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
            onStartedAsync: SendAckAndRecordAsync,
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

        if (executionResult.Started != null)
            correlationId = executionResult.Started.CommandId;
        return;

        async ValueTask SendAguiEventAndRecordAsync(WorkflowOutputFrame frame, CancellationToken token)
        {
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateAguiEvent(
                    command.RequestId, frame, context.CorrelationId),
                token,
                responseMessageType);
            scope.RecordFirstResponse();
        }

        async ValueTask SendAckAndRecordAsync(WorkflowChatRunStarted started, CancellationToken token)
        {
            correlationId = started.CommandId;
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, started),
                token,
                responseMessageType);
            scope.RecordFirstResponse();
        }
    }
}
