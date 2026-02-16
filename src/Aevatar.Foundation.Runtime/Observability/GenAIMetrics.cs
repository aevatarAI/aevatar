// ─────────────────────────────────────────────────────────────
// GenAIMetrics - OpenTelemetry GenAI Semantic Conventions metrics.
// Token usage, operation duration, tool invocation duration.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics.Metrics;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>GenAI semantic convention metrics for LLM and tool operations.</summary>
public static class GenAIMetrics
{
    private static readonly Meter Meter = new("Aevatar.GenAI", "1.0.0");

    /// <summary>Token usage histogram (input + output tokens per LLM call).</summary>
    public static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", "tokens",
            "Number of tokens consumed per LLM call");

    /// <summary>LLM call duration histogram in milliseconds.</summary>
    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "ms",
            "Duration of LLM operations in milliseconds");

    /// <summary>Tool invocation duration histogram in milliseconds.</summary>
    public static readonly Histogram<double> ToolInvocationDuration =
        Meter.CreateHistogram<double>("aevatar.tool.invocation.duration", "ms",
            "Duration of tool invocations in milliseconds");

    /// <summary>Agent invocation counter.</summary>
    public static readonly Counter<long> AgentInvocations =
        Meter.CreateCounter<long>("aevatar.agent.invocations",
            description: "Number of agent invocations");
}
