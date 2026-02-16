using System.Net.WebSockets;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Host.Api.Endpoints;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        IWorkflowChatRunApplicationService chatRunService,
        CancellationToken ct = default)
    {
        var request = new WorkflowChatRunRequest(
            command.Input.Prompt,
            command.Input.Workflow,
            command.Input.AgentId);

        var executionResult = await chatRunService.ExecuteAsync(
            request,
            (frame, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "agui.event",
                requestId = command.RequestId,
                payload = frame,
            }, token)),
            onStartedAsync: (started, token) => new ValueTask(ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.ack",
                requestId = command.RequestId,
                payload = new
                {
                    runId = started.RunId,
                    threadId = started.ActorId,
                    workflow = started.WorkflowName,
                },
            }, token)),
            ct);

        if (executionResult.Error != WorkflowChatRunStartError.None)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(executionResult.Error);
            await ChatWebSocketProtocol.SendAsync(socket, new
            {
                type = "command.error",
                requestId = command.RequestId,
                code,
                message,
            }, ct);
            return;
        }

        var started = executionResult.Started!;
        var finalize = executionResult.FinalizeResult;
        await ChatWebSocketProtocol.SendAsync(socket, new
        {
            type = "query.result",
            requestId = command.RequestId,
            payload = new
            {
                runId = started.RunId,
                projectionCompletionStatus = finalize?.ProjectionCompletionStatus.ToString(),
                projectionCompleted = finalize?.ProjectionCompleted ?? false,
                report = finalize?.Report,
            },
        }, ct);
    }
}
