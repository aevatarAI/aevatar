using System.Collections.Generic;
using System.Text.Json;
using Aevatar.Workflow.Sdk.Internal;

namespace Aevatar.Workflow.Sdk.Contracts;

public static class WorkflowCustomEventNames
{
    public const string RunContext = "aevatar.run.context";
    public const string StepRequest = "aevatar.step.request";
    public const string StepCompleted = "aevatar.step.completed";
    public const string HumanInputRequest = "aevatar.human_input.request";
    public const string WaitingSignal = "aevatar.workflow.waiting_signal";
    public const string LlmReasoning = "aevatar.llm.reasoning";
}

public sealed record WorkflowRunContextEventData
{
    public string? ActorId { get; init; }
    public string? WorkflowName { get; init; }
    public string? CommandId { get; init; }
}

public sealed record WorkflowStepRequestEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public string? Input { get; init; }
    public string? TargetRole { get; init; }
}

public sealed record WorkflowStepCompletedEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public bool? Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
}

public sealed record WorkflowHumanInputRequestEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? SuspensionType { get; init; }
    public string? Prompt { get; init; }
    public int? TimeoutSeconds { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
}

public sealed record WorkflowWaitingSignalEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? SignalName { get; init; }
    public string? Prompt { get; init; }
    public int? TimeoutMs { get; init; }
}

public sealed record WorkflowLlmReasoningEventData
{
    public string? Role { get; init; }
    public string? Delta { get; init; }
}

public static class WorkflowCustomEventParser
{
    public static bool TryParseRunContext(WorkflowOutputFrame frame, out WorkflowRunContextEventData data) =>
        TryParseRunContext(frame.Name, frame.Value, out data);

    public static bool TryParseRunContext(string? customEventName, JsonElement? value, out WorkflowRunContextEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.RunContext) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowRunContextEventData
        {
            ActorId = WorkflowSdkJson.TryReadString(obj, "actorId", "ActorId"),
            WorkflowName = WorkflowSdkJson.TryReadString(obj, "workflowName", "WorkflowName"),
            CommandId = WorkflowSdkJson.TryReadString(obj, "commandId", "CommandId"),
        };
        return true;
    }

    public static bool TryParseStepRequest(WorkflowOutputFrame frame, out WorkflowStepRequestEventData data) =>
        TryParseStepRequest(frame.Name, frame.Value, out data);

    public static bool TryParseStepRequest(string? customEventName, JsonElement? value, out WorkflowStepRequestEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.StepRequest) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowStepRequestEventData
        {
            RunId = WorkflowSdkJson.TryReadString(obj, "runId", "RunId"),
            StepId = WorkflowSdkJson.TryReadString(obj, "stepId", "StepId"),
            StepType = WorkflowSdkJson.TryReadString(obj, "stepType", "StepType"),
            Input = WorkflowSdkJson.TryReadString(obj, "input", "Input"),
            TargetRole = WorkflowSdkJson.TryReadString(obj, "targetRole", "TargetRole"),
        };
        return true;
    }

    public static bool TryParseStepCompleted(WorkflowOutputFrame frame, out WorkflowStepCompletedEventData data) =>
        TryParseStepCompleted(frame.Name, frame.Value, out data);

    public static bool TryParseStepCompleted(string? customEventName, JsonElement? value, out WorkflowStepCompletedEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.StepCompleted) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowStepCompletedEventData
        {
            RunId = WorkflowSdkJson.TryReadString(obj, "runId", "RunId"),
            StepId = WorkflowSdkJson.TryReadString(obj, "stepId", "StepId"),
            Success = TryReadBoolean(obj, "success", "Success"),
            Output = WorkflowSdkJson.TryReadString(obj, "output", "Output"),
            Error = WorkflowSdkJson.TryReadString(obj, "error", "Error"),
        };
        return true;
    }

    public static bool TryParseHumanInputRequest(WorkflowOutputFrame frame, out WorkflowHumanInputRequestEventData data) =>
        TryParseHumanInputRequest(frame.Name, frame.Value, out data);

    public static bool TryParseHumanInputRequest(string? customEventName, JsonElement? value, out WorkflowHumanInputRequestEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.HumanInputRequest) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowHumanInputRequestEventData
        {
            RunId = WorkflowSdkJson.TryReadString(obj, "runId", "RunId"),
            StepId = WorkflowSdkJson.TryReadString(obj, "stepId", "StepId"),
            SuspensionType = WorkflowSdkJson.TryReadString(obj, "suspensionType", "SuspensionType"),
            Prompt = WorkflowSdkJson.TryReadString(obj, "prompt", "Prompt"),
            TimeoutSeconds = TryReadInt(obj, "timeoutSeconds", "TimeoutSeconds"),
            Metadata = TryReadStringMap(obj, "metadata", "Metadata"),
        };
        return true;
    }

    public static bool TryParseWaitingSignal(WorkflowOutputFrame frame, out WorkflowWaitingSignalEventData data) =>
        TryParseWaitingSignal(frame.Name, frame.Value, out data);

    public static bool TryParseWaitingSignal(string? customEventName, JsonElement? value, out WorkflowWaitingSignalEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.WaitingSignal) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowWaitingSignalEventData
        {
            RunId = WorkflowSdkJson.TryReadString(obj, "runId", "RunId"),
            StepId = WorkflowSdkJson.TryReadString(obj, "stepId", "StepId"),
            SignalName = WorkflowSdkJson.TryReadString(obj, "signalName", "SignalName"),
            Prompt = WorkflowSdkJson.TryReadString(obj, "prompt", "Prompt"),
            TimeoutMs = TryReadInt(obj, "timeoutMs", "TimeoutMs"),
        };
        return true;
    }

    public static bool TryParseLlmReasoning(WorkflowOutputFrame frame, out WorkflowLlmReasoningEventData data) =>
        TryParseLlmReasoning(frame.Name, frame.Value, out data);

    public static bool TryParseLlmReasoning(string? customEventName, JsonElement? value, out WorkflowLlmReasoningEventData data)
    {
        if (!Is(customEventName, WorkflowCustomEventNames.LlmReasoning) || !TryGetObject(value, out var obj))
        {
            data = default!;
            return false;
        }

        data = new WorkflowLlmReasoningEventData
        {
            Role = WorkflowSdkJson.TryReadString(obj, "role", "Role"),
            Delta = WorkflowSdkJson.TryReadString(obj, "delta", "Delta"),
        };
        return true;
    }

    private static bool Is(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool TryGetObject(JsonElement? value, out JsonElement obj)
    {
        if (value.HasValue && value.Value.ValueKind == JsonValueKind.Object)
        {
            obj = value.Value;
            return true;
        }

        obj = default;
        return false;
    }

    private static Dictionary<string, string>? TryReadStringMap(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                continue;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return result;
        }

        return null;
    }

    private static bool? TryReadBoolean(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int? TryReadInt(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
                return n;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
