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
        CancellationToken ct = default)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);

        var request = new WorkflowChatRunRequest(
            command.Input.Prompt,
            command.Input.Workflow,
            command.Input.AgentId);

        var executionResult = await chatRunService.ExecuteAsync(
            request,
            (frame, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateAguiEvent(command.RequestId, frame),
                token,
                responseMessageType)),
            onStartedAsync: (started, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, started),
                token,
                responseMessageType)),
            ct);

        if (executionResult.Error != WorkflowChatRunStartError.None)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(executionResult.Error);
            await ChatWebSocketProtocol.SendAsync(
                socket,
                ChatWebSocketEnvelopeFactory.CreateCommandError(command.RequestId, code, message),
                ct,
                responseMessageType);
            return;
        }
    }
}
