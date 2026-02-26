using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Hooks;
using Google.Protobuf;

namespace Aevatar.AI.Core.Hooks.BuiltIn;

/// <summary>Publishes ToolCallEvent / ToolResultEvent to the actor event bus during tool execution.</summary>
public sealed class ToolCallEventPublishingHook : IAIGAgentExecutionHook
{
    private readonly Func<IMessage, Task> _publish;

    public ToolCallEventPublishingHook(Func<IMessage, Task> publish) =>
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));

    public string Name => "tool_call_event_publisher";
    public int Priority => 200; // After trace and truncation

    public async Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.ToolName)) return;

        await _publish(new ToolCallEvent
        {
            ToolName = ctx.ToolName,
            ArgumentsJson = ctx.ToolArguments ?? "",
            CallId = ctx.ToolCallId ?? "",
        });
    }

    public async Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.ToolCallId)) return;

        await _publish(new ToolResultEvent
        {
            CallId = ctx.ToolCallId,
            ResultJson = ctx.ToolResult ?? "",
            Success = true,
        });
    }
}
