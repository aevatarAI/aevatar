using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Observatory;

// 06-19-workflow-run-observatory (C2), spec §7: pure stage -> AGUI-shaped view event mapping over the
// committed run-report timeline. No I/O, no token-level deltas (D1) — role replies arrive as one chunk.
// Tool-call detail (arguments/result/success/error) is read from the committed tool.call entry's data map
// (already redacted at the materialization boundary; spec §8 / O1). Usage totals come from the report's
// authoritative per-step usage, not from the timeline data.
public static class WorkflowRunObservatoryTimelineMapper
{
    public static ObservatoryViewEvent ToViewEvent(WorkflowRunTimelineEvent item, string roleReplyContent = "")
    {
        ArgumentNullException.ThrowIfNull(item);

        var stage = item.Stage ?? string.Empty;
        return new ObservatoryViewEvent
        {
            Kind = MapKind(stage),
            TimestampUtc = item.Timestamp,
            Stage = stage,
            Message = item.Message ?? string.Empty,
            AgentId = item.AgentId ?? string.Empty,
            StepId = item.StepId ?? string.Empty,
            StepType = item.StepType ?? string.Empty,
            ToolCall = stage == "tool.call" ? BuildToolCall(item) : null,
            Content = roleReplyContent ?? string.Empty,
            Data = item.Data ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    public static ObservatoryUsageTotals ToUsageTotals(WorkflowRunUsageMetrics usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new ObservatoryUsageTotals
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            Cost = usage.Cost,
        };
    }

    private static string MapKind(string stage) =>
        stage switch
        {
            "workflow.start" => "RunStarted",
            "step.request" => "StepStarted",
            "role.reply" => "Message",
            "tool.call" => "ToolCall",
            "step.completed" => "StepFinished",
            "step.failed" => "RunError",
            "workflow.failed" => "RunError",
            "workflow.suspended" => "HumanInputRequest",
            "signal.waiting" => "SignalWaiting",
            "signal.buffered" => "SignalBuffered",
            "workflow.completed" => "RunFinished",
            "workflow.stopped" => "RunStopped",
            _ => "Event",
        };

    private static ObservatoryToolCallDetail BuildToolCall(WorkflowRunTimelineEvent item)
    {
        var data = item.Data;
        return new ObservatoryToolCallDetail
        {
            ToolName = item.Message ?? string.Empty,
            CallId = ReadData(data, "call_id"),
            ArgumentsJson = ReadData(data, "arguments_json"),
            ResultJson = ReadData(data, "result_json"),
            Success = string.Equals(ReadData(data, "success"), "true", StringComparison.OrdinalIgnoreCase),
            Error = ReadData(data, "error"),
        };
    }

    private static string ReadData(IReadOnlyDictionary<string, string> data, string key) =>
        data != null && data.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
}
