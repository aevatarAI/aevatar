using System.Globalization;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatTaskPlanJsonFormatter
{
    private const long MaxBrowserSafeInteger = 9_007_199_254_740_991;

    public static JsonNode FormatTaskPlan(IMessage payload)
    {
        var node = FormatProtobuf(payload);
        IncludeWireRequiredBooleans(payload, node);
        NormalizeBrowserSafeIntegers(node);
        return node;
    }

    public static JsonNode FormatProtobuf(IMessage payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var node = JsonNode.Parse(JsonFormatter.Default.Format(payload))
                   ?? throw new InvalidOperationException(
                       "Typed custom payload must serialize to JSON.");
        NormalizeNyxIdEnumValues(node);
        return node;
    }

    private static void IncludeWireRequiredBooleans(IMessage payload, JsonNode node)
    {
        switch (payload)
        {
            case NyxIdChatTaskPlan plan when node["steps"] is JsonArray steps:
                if (steps.Count != plan.Steps.Count)
                    throw new InvalidOperationException("TaskPlan step serialization is inconsistent.");

                for (var index = 0; index < plan.Steps.Count; index++)
                    IncludeWireRequiredBooleans(steps[index], plan.Steps[index]);
                break;
            case NyxIdChatTaskPlanStepChanged changed when changed.Step is not null:
                IncludeWireRequiredBooleans(node["step"], changed.Step);
                break;
        }
    }

    private static void IncludeWireRequiredBooleans(
        JsonNode? node,
        NyxIdChatTaskPlanStep step)
    {
        if (node is not JsonObject stepNode)
            throw new InvalidOperationException("TaskPlan step must serialize to a JSON object.");

        stepNode["required"] = step.Required;
        stepNode["mayChangeExternalState"] = step.MayChangeExternalState;
        if (step.Operation is not null)
        {
            if (stepNode["operation"] is not JsonObject operationNode)
            {
                throw new InvalidOperationException(
                    "TaskPlan step operation must serialize to a JSON object.");
            }

            operationNode["mayChangeExternalState"] = step.Operation.MayChangeExternalState;
        }
    }

    private static void NormalizeBrowserSafeIntegers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonValue value &&
                        IsBrowserNumberField(property.Key) &&
                        value.TryGetValue<string>(out var text) &&
                        long.TryParse(
                            text,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var parsed))
                    {
                        if (parsed is < -MaxBrowserSafeInteger or > MaxBrowserSafeInteger)
                        {
                            throw new InvalidOperationException(
                                $"TaskPlan field '{property.Key}' exceeds the browser-safe integer range.");
                        }
                        obj[property.Key] = parsed;
                    }
                    else if (property.Value is not null)
                    {
                        NormalizeBrowserSafeIntegers(property.Value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                        NormalizeBrowserSafeIntegers(item);
                }
                break;
        }
    }

    private static bool IsBrowserNumberField(string propertyName) =>
        propertyName is
            "operationGeneration" or
            "latestProgressSequence" or
            "suggestedThreshold" or
            "effectiveThreshold" or
            "observedValue" or
            "suggestedValue" or
            "minimumValue" or
            "maximumValue";

    private static void NormalizeNyxIdEnumValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonValue value &&
                        value.TryGetValue<string>(out var text) &&
                        TryNormalizeNyxIdEnumValue(text, out var normalized))
                    {
                        obj[property.Key] = normalized;
                    }
                    else if (property.Value is not null)
                    {
                        NormalizeNyxIdEnumValues(property.Value);
                    }
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value &&
                        value.TryGetValue<string>(out var text) &&
                        TryNormalizeNyxIdEnumValue(text, out var normalized))
                    {
                        array[index] = normalized;
                    }
                    else if (array[index] is not null)
                    {
                        NormalizeNyxIdEnumValues(array[index]!);
                    }
                }
                break;
        }
    }

    private static bool TryNormalizeNyxIdEnumValue(string value, out string normalized)
    {
        normalized = value switch
        {
            "TOOL_PRESENTATION_KIND_GENERIC" => "generic",
            "TOOL_PRESENTATION_KIND_BUILT_IN" => "builtIn",
            "TOOL_PRESENTATION_KIND_NYX_ID_OPERATION" => "nyxIdOperation",
            "TOOL_PRESENTATION_KIND_MCP" => "mcp",
            "TOOL_PRESENTATION_KIND_SKILL" => "skill",
            "TOOL_AVAILABILITY_AVAILABLE" => "available",
            "TOOL_AVAILABILITY_UNAVAILABLE" => "unavailable",
            _ => string.Empty,
        };
        if (normalized.Length > 0)
            return true;

        string[] prefixes =
        [
            "NYX_ID_APPROVAL_DECISION_MODE_",
            "AGENT_TOOL_RECEIPT_STATUS_",
            "NYX_ID_CHAT_CONTINUATION_ADMISSION_STATUS_",
            "NYX_ID_CHAT_STEP_CONTROL_KIND_",
            "NYX_ID_CHAT_ACTION_DISPOSITION_",
            "NYX_ID_CHAT_OPERATION_PHASE_",
            "NYX_ID_CHAT_EFFECT_EVIDENCE_",
            "NYX_ID_CHAT_TRANSITION_OUTCOME_",
            "NYX_ID_CHAT_CONTINUATION_KIND_",
            "NYX_ID_CHAT_CONTROL_OUTCOME_",
            "NYX_ID_ASSISTANT_ACTION_RISK_",
            "NYX_ID_ASSISTANT_ACTION_TIER_",
            "NYX_ID_ASSISTANT_ACTION_KIND_",
            "NYX_ID_CHAT_CONTROL_KIND_",
            "NYX_ID_CHAT_TURN_STATUS_",
            "NYX_ID_CHAT_TASK_STATUS_",
            "NYX_ID_CHAT_STEP_STATUS_",
            "NYX_ID_CHAT_STEP_KIND_",
            "NYX_ID_CHAT_PLAN_GATE_MODE_",
            "NYX_ID_CHAT_PLAN_GATE_STATUS_",
            "NYX_ID_CHAT_STEP_ADDED_BY_",
            "NYX_ID_CHAT_PLAN_REVISION_CAUSE_",
            "NYX_ID_CHAT_STEP_ESTIMATE_KIND_",
            "NYX_ID_CHAT_SUBSTEP_STATUS_",
            "NYX_ID_CHAT_STEP_CHANGE_KIND_",
            "NYX_ID_CHAT_ATTENTION_KIND_",
            "NYX_ID_CHAT_APPROVAL_REVERSIBILITY_",
            "NYX_ID_CHAT_NEEDS_YOU_RESOLUTION_OUTCOME_",
            "NYX_ID_CHAT_INTEGER_COMPARISON_",
            "NYX_ID_CHAT_CONDITION_OUTCOME_",
            "NYX_ID_CHAT_THRESHOLD_ORIGIN_",
        ];
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            normalized = value[prefix.Length..].ToLowerInvariant();
            return true;
        }

        normalized = value;
        return false;
    }
}
