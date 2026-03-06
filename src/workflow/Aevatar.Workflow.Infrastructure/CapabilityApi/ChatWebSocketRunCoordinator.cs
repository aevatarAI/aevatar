using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        CancellationToken ct = default,
        WorkflowCapabilitiesDocument? capabilities = null)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
        var correlationId = string.Empty;
        CapabilityMessageTraceContext ResolveContext() =>
            CapabilityTraceContext.CreateMessageContext(correlationId, command.RequestId);

        var normalizedRequest = ChatRunRequestNormalizer.Normalize(command.Input, capabilities);
        if (!normalizedRequest.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandError(
                    command.RequestId,
                    code,
                    message,
                    context.CorrelationId),
                ct,
                responseMessageType);
            return;
        }

        var executionResult = await chatRunService.ExecuteAsync(
            normalizedRequest.Request!,
            (frame, token) =>
            {
                var context = ResolveContext();
                return new ValueTask(ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateAguiEvent(
                        command.RequestId,
                        frame,
                        context.CorrelationId),
                    token,
                    responseMessageType));
            },
            onStartedAsync: (started, token) =>
            {
                correlationId = started.CommandId;
                return new ValueTask(ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, started),
                    token,
                    responseMessageType));
            },
            ct);

        if (executionResult.Error != WorkflowChatRunStartError.None)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(executionResult.Error);
            var context = ResolveContext();
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandError(
                    command.RequestId,
                    code,
                    message,
                    context.CorrelationId),
                ct,
                responseMessageType);
            return;
        }

        if (executionResult.Started != null)
            correlationId = executionResult.Started.CommandId;
    }
}
