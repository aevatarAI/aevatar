using System.Collections.Generic;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;

namespace Aevatar.Workflow.Sdk.Contracts;

public static class WorkflowCustomEventNames
{
    public const string RunContext = "aevatar.run.context";
    public const string StepRequest = "aevatar.step.request";
    public const string StepCompleted = "aevatar.step.completed";
    public const string HumanInputRequest = "aevatar.human_input.request";
    public const string WaitingSignal = "aevatar.workflow.waiting_signal";
    public const string SignalBuffered = "aevatar.workflow.signal.buffered";
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
    public IDictionary<string, string>? Annotations { get; init; }
    public string? NextStepId { get; init; }
    public string? BranchKey { get; init; }
    public string? AssignedVariable { get; init; }
    public string? AssignedValue { get; init; }
}

public sealed record WorkflowHumanInputRequestEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? SuspensionType { get; init; }
    public string? Prompt { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? VariableName { get; init; }
    public string? Content { get; init; }
    public string? DeliveryTargetId { get; init; }
    public bool? Secure { get; init; }
    public string? RedactedOutput { get; init; }
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

public sealed record WorkflowSignalBufferedEventData
{
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? SignalName { get; init; }
    public string? Payload { get; init; }
    public long? ReceivedAtUnixTimeMs { get; init; }
}

public static class WorkflowCustomEventParser
{
    public static bool TryParseRunContext(WorkflowRunEventEnvelope frame, out WorkflowRunContextEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowRunContextPayload>(frame, WorkflowCustomEventNames.RunContext, out var payload))
        {
            data = new WorkflowRunContextEventData
            {
                ActorId = payload.ActorId,
                WorkflowName = payload.WorkflowName,
                CommandId = payload.CommandId,
            };
            return true;
        }

        data = default!;
        return false;
    }

    public static bool TryParseStepRequest(WorkflowRunEventEnvelope frame, out WorkflowStepRequestEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowStepRequestCustomPayload>(frame, WorkflowCustomEventNames.StepRequest, out var payload))
        {
            data = new WorkflowStepRequestEventData
            {
                RunId = payload.RunId,
                StepId = payload.StepId,
                StepType = payload.StepType,
                Input = payload.Input,
                TargetRole = payload.TargetRole,
            };
            return true;
        }

        data = default!;
        return false;
    }

    public static bool TryParseStepCompleted(WorkflowRunEventEnvelope frame, out WorkflowStepCompletedEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowStepCompletedCustomPayload>(frame, WorkflowCustomEventNames.StepCompleted, out var payload))
        {
            data = new WorkflowStepCompletedEventData
            {
                RunId = payload.RunId,
                StepId = payload.StepId,
                Success = payload.Success,
                Output = payload.Output,
                Error = payload.Error,
                Annotations = new Dictionary<string, string>(payload.Annotations),
                NextStepId = payload.NextStepId,
                BranchKey = payload.BranchKey,
                AssignedVariable = payload.AssignedVariable,
                AssignedValue = payload.AssignedValue,
            };
            return true;
        }

        data = default!;
        return false;
    }

    public static bool TryParseHumanInputRequest(WorkflowRunEventEnvelope frame, out WorkflowHumanInputRequestEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowHumanInputRequestCustomPayload>(frame, WorkflowCustomEventNames.HumanInputRequest, out var payload))
        {
            data = new WorkflowHumanInputRequestEventData
            {
                RunId = payload.RunId,
                StepId = payload.StepId,
                SuspensionType = payload.SuspensionType,
                Prompt = payload.Prompt,
                TimeoutSeconds = payload.TimeoutSeconds,
                VariableName = payload.VariableName,
                Content = payload.Content,
                DeliveryTargetId = payload.DeliveryTargetId,
                Secure = payload.Secure,
                RedactedOutput = payload.RedactedOutput,
                Metadata = FilterReservedHumanInputMetadata(payload.Metadata),
            };
            return true;
        }

        data = default!;
        return false;
    }

    private static Dictionary<string, string>? FilterReservedHumanInputMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null)
            return null;

        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (key is "variable" or "secure" or "input_mode" or "redacted_output")
                continue;

            filtered[key] = value;
        }

        return filtered;
    }

    public static bool TryParseWaitingSignal(WorkflowRunEventEnvelope frame, out WorkflowWaitingSignalEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowWaitingSignalCustomPayload>(frame, WorkflowCustomEventNames.WaitingSignal, out var payload))
        {
            data = new WorkflowWaitingSignalEventData
            {
                RunId = payload.RunId,
                StepId = payload.StepId,
                SignalName = payload.SignalName,
                Prompt = payload.Prompt,
                TimeoutMs = payload.TimeoutMs,
            };
            return true;
        }

        data = default!;
        return false;
    }

    public static bool TryParseSignalBuffered(WorkflowRunEventEnvelope frame, out WorkflowSignalBufferedEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowSignalBufferedCustomPayload>(frame, WorkflowCustomEventNames.SignalBuffered, out var payload))
        {
            data = new WorkflowSignalBufferedEventData
            {
                RunId = payload.RunId,
                StepId = payload.StepId,
                SignalName = payload.SignalName,
                Payload = payload.Payload,
                ReceivedAtUnixTimeMs = payload.ReceivedAtUnixTimeMs,
            };
            return true;
        }

        data = default!;
        return false;
    }

    public static bool TryParseLlmReasoning(WorkflowRunEventEnvelope frame, out WorkflowLlmReasoningEventData data)
    {
        if (TryUnpackCustomPayload<WorkflowReasoningCustomPayload>(frame, WorkflowCustomEventNames.LlmReasoning, out var payload))
        {
            data = new WorkflowLlmReasoningEventData
            {
                Role = payload.Role,
                Delta = payload.Delta,
            };
            return true;
        }

        data = default!;
        return false;
    }

    private static bool TryUnpackCustomPayload<TPayload>(
        WorkflowRunEventEnvelope frame,
        string customEventName,
        out TPayload payload)
        where TPayload : class, IMessage<TPayload>, new()
    {
        ArgumentNullException.ThrowIfNull(frame);
        var custom = frame.Custom;
        if (frame.EventCase != WorkflowRunEventEnvelope.EventOneofCase.Custom ||
            custom == null ||
            !Is(custom.Name, customEventName) ||
            custom.Payload?.Is(new TPayload().Descriptor) != true)
        {
            payload = default!;
            return false;
        }

        payload = custom.Payload.Unpack<TPayload>();
        return true;
    }

    private static bool Is(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
