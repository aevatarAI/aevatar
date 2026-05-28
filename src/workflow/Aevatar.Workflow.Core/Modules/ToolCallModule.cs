using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    // Refactor (iter110/cluster-1): Old pattern: tool_call discovered/executed tools and published StepCompletedEvent inside the actor/module turn.  New principle: tool_call publishes typed tool intent and only typed continuation results complete the step.
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "tool_call")
            return;

        var toolName = request.Parameters.GetValueOrDefault("tool", string.Empty).Trim();
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new ToolCallContinuationResultEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                ExecutionId = request.ExecutionId,
                Success = false,
                Error = "tool_call 缺少 tool 参数",
            }, TopologyAudience.Self, ct);
            return;
        }

        var argumentsJson = string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
        ctx.Logger.LogInformation("ToolCall: {StepId} typed intent for tool {Tool}", request.StepId, toolName);

        await ctx.PublishAsync(new ToolCallEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = request.StepId,
        }, TopologyAudience.Self, ct);

        var intent = new ToolCallIntentEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
        };
        await ctx.PublishAsync(intent, TopologyAudience.Self, ct);
    }
}
