// ─────────────────────────────────────────────────────────────
// AevatarActivitySource - distributed tracing ActivitySource.
// Emits spans following OpenTelemetry GenAI Semantic Conventions:
//   invoke_agent, chat, execute_tool
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>ActivitySource for Aevatar agent distributed tracing (GenAI conventions).</summary>
public static class AevatarActivitySource
{
    /// <summary>ActivitySource instance.</summary>
    public static readonly ActivitySource Source = new("Aevatar.Agents", "1.0.0");

    /// <summary>Global sensitive data flag.</summary>
    public static bool EnableSensitiveData { get; set; }

    /// <summary>Starts a HandleEvent activity (legacy, used by LocalActor).</summary>
    public static Activity? StartHandleEvent(string agentId, string eventId) =>
        Source.StartActivity($"HandleEvent {agentId}")
            ?.SetTag("aevatar.agent.id", agentId)
            ?.SetTag("aevatar.event.id", eventId);

    /// <summary>Starts an invoke_agent span per GenAI semantic conventions.</summary>
    public static Activity? StartInvokeAgent(string agentId, string? agentName = null, string? system = null)
    {
        var activity = Source.StartActivity($"invoke_agent {agentName ?? agentId}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        activity.SetTag("gen_ai.agent.id", agentId);
        if (agentName != null) activity.SetTag("gen_ai.agent.name", agentName);
        if (system != null) activity.SetTag("gen_ai.system", system);

        return activity;
    }

    /// <summary>Starts a chat span per GenAI semantic conventions.</summary>
    public static Activity? StartChat(string? model = null, string? system = null)
    {
        var activity = Source.StartActivity($"chat {model ?? "unknown"}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "chat");
        if (model != null) activity.SetTag("gen_ai.request.model", model);
        if (system != null) activity.SetTag("gen_ai.system", system);

        return activity;
    }

    /// <summary>Records token usage on a chat span.</summary>
    public static void RecordTokenUsage(Activity? activity, int? inputTokens, int? outputTokens)
    {
        if (activity == null) return;
        if (inputTokens.HasValue) activity.SetTag("gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue) activity.SetTag("gen_ai.usage.output_tokens", outputTokens.Value);
    }

    /// <summary>Starts an execute_tool span per GenAI semantic conventions.</summary>
    public static Activity? StartExecuteTool(string toolName, string? toolCallId = null)
    {
        var activity = Source.StartActivity($"execute_tool {toolName}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.name", toolName);
        if (toolCallId != null) activity.SetTag("gen_ai.tool.call_id", toolCallId);

        return activity;
    }

    /// <summary>Records sensitive data (prompt/response) on a span if enabled.</summary>
    public static void RecordSensitiveData(Activity? activity, string key, string? value)
    {
        if (activity == null || !EnableSensitiveData || value == null) return;
        activity.SetTag(key, value);
    }
}
