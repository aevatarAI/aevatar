using System.Net.WebSockets;
using System.Diagnostics;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatWebSocketRunCoordinator
{
    public static async Task<double?> ExecuteAsync(
        WebSocket socket,
        ChatWebSocketCommandEnvelope command,
        ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> chatRunService,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var firstResponseDurationMs = (double?)null;
        void RecordFirstResponseIfNeeded()
        {
            if (firstResponseDurationMs.HasValue)
                return;
            firstResponseDurationMs = stopwatch.Elapsed.TotalMilliseconds;
        }

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
                RecordFirstResponseIfNeeded();
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
                RecordFirstResponseIfNeeded();
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
            RecordFirstResponseIfNeeded();
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
            return firstResponseDurationMs;
        }

        correlationId = executionResult.Started!.CommandId;
        return firstResponseDurationMs;
    }
}
