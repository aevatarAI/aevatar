// ─────────────────────────────────────────────────────────────
// GenAIActivitySource — AI 层 OpenTelemetry GenAI 语义规范
// 提供 invoke_agent / chat / execute_tool 标准化 span
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.AI.Core.Observability;

/// <summary>
/// GenAI semantic convention ActivitySource and Meter for the AI layer.
/// Emits standardized spans and metrics per OpenTelemetry GenAI conventions.
/// </summary>
public static class GenAIActivitySource
{
    public static readonly ActivitySource Source = new("Aevatar.GenAI", "1.0.0");
    private static readonly Meter Meter = new("Aevatar.GenAI", "1.0.0");

    /// <summary>When true, prompts/responses are included in spans.</summary>
    public static bool EnableSensitiveData { get; set; }

    // ─── Metrics ───

    /// <summary>Token usage histogram per LLM call.</summary>
    public static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", "tokens");

    /// <summary>LLM call duration in ms.</summary>
    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "ms");

    /// <summary>Tool invocation duration in ms.</summary>
    public static readonly Histogram<double> ToolInvocationDuration =
        Meter.CreateHistogram<double>("aevatar.tool.invocation.duration", "ms");

    // ─── Span factories ───

    public static Activity? StartInvokeAgent(string? agentId, string? agentName = null)
    {
        var activity = Source.StartActivity($"invoke_agent {agentName ?? agentId ?? "unknown"}", ActivityKind.Client);
        if (activity == null) return null;
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        if (agentId != null) activity.SetTag("gen_ai.agent.id", agentId);
        if (agentName != null) activity.SetTag("gen_ai.agent.name", agentName);
        return activity;
    }

    public static Activity? StartChat(string? model = null)
    {
        var activity = Source.StartActivity($"chat {model ?? "unknown"}", ActivityKind.Client);
        if (activity == null) return null;
        activity.SetTag("gen_ai.operation.name", "chat");
        if (model != null) activity.SetTag("gen_ai.request.model", model);
        return activity;
    }

    public static Activity? StartExecuteTool(string toolName, string? callId = null)
    {
        // Per OpenTelemetry GenAI semantic conventions, execute_tool spans should be INTERNAL.
        var activity = Source.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
        if (activity == null) return null;
        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.name", toolName);
        if (callId != null) activity.SetTag("gen_ai.tool.call_id", callId);
        return activity;
    }
}
