using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdChatConditionProposal(
    string SourceInputRequestId,
    long ObservedValue,
    string GuardedToolName);

internal static class NyxIdChatConditionEvaluateContract
{
    internal const string ToolName = "condition_evaluate";

    internal static bool IsConditionEvaluate(NyxIdChatToolCall? call) =>
        call is not null &&
        string.Equals(call.ToolName?.Trim(), ToolName, StringComparison.Ordinal);

    internal static bool TryParse(
        string? argumentsJson,
        out NyxIdChatConditionProposal proposal)
    {
        proposal = null!;
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(static property => property.Name is not
                    ("source_input_request_id" or "observed_value" or "guarded_tool_name")) ||
                !root.TryGetProperty("source_input_request_id", out var source) ||
                source.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(source.GetString()) ||
                !root.TryGetProperty("observed_value", out var observed) ||
                observed.ValueKind != JsonValueKind.Number ||
                !observed.TryGetInt64(out var observedValue) ||
                !root.TryGetProperty("guarded_tool_name", out var guardedTool) ||
                guardedTool.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(guardedTool.GetString()) ||
                string.Equals(guardedTool.GetString()?.Trim(), ToolName, StringComparison.Ordinal) ||
                string.Equals(guardedTool.GetString()?.Trim(), NyxIdChatAskUserContract.ToolName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            proposal = new NyxIdChatConditionProposal(
                source.GetString()!.Trim(),
                observedValue,
                guardedTool.GetString()!.Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
