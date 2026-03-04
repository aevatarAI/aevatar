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
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
        var correlationId = string.Empty;
        CapabilityMessageTraceContext ResolveContext() =>
            CapabilityTraceContext.CreateMessageContext(correlationId, command.RequestId);

        var request = new WorkflowChatRunRequest(
            command.Input.Prompt,
            command.Input.Workflow,
            command.Input.AgentId,
            command.Input.WorkflowYaml);

        var executionResult = await chatRunService.ExecuteAsync(
            request,
            (frame, token) =>
            {
                var context = ResolveContext();
                return new ValueTask(ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateAguiEvent(
                        command.RequestId,
                        frame,
                        context.CorrelationId,
                        context.TraceId),
                    token,
                    responseMessageType));
            },
            onStartedAsync: (started, token) =>
            {
                correlationId = started.CommandId;
                var context = ResolveContext();
                return new ValueTask(ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandAck(command.RequestId, started, context.TraceId),
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
                    context.CorrelationId,
                    context.TraceId),
                ct,
                responseMessageType);
            return;
        }

        var started = executionResult.Started!;
        var finalize = executionResult.FinalizeResult;
        correlationId = started.CommandId;
        var messageContext = ResolveContext();
        var snapshot = await queryService.GetActorSnapshotAsync(started.ActorId, ct);
        await ChatWebSocketProtocol.SendAsync(
            socket,
            new
            {
                type = "query.result",
                requestId = command.RequestId,
                correlationId = messageContext.CorrelationId,
                traceId = messageContext.TraceId,
                payload = new
                {
                    commandId = started.CommandId,
                    actorId = started.ActorId,
                    projectionCompletionStatus = finalize?.ProjectionCompletionStatus.ToString(),
                    projectionCompleted = finalize?.ProjectionCompleted ?? false,
                    snapshot,
                },
            },
            ct,
            responseMessageType);
    }
}
