using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Session event codec for workflow live run output events.
/// </summary>
public sealed class WorkflowRunEventSessionCodec : IProjectionSessionEventCodec<WorkflowRunEvent>
{
    public string Channel => "workflow-run";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, Func<string, WorkflowRunEvent?>> Deserializers =
        new Dictionary<string, Func<string, WorkflowRunEvent?>>(StringComparer.Ordinal)
        {
            ["RUN_STARTED"] = payload => DeserializeEvent<WorkflowRunStartedEvent>(payload),
            ["RUN_FINISHED"] = payload => DeserializeEvent<WorkflowRunFinishedEvent>(payload),
            ["RUN_ERROR"] = payload => DeserializeEvent<WorkflowRunErrorEvent>(payload),
            ["STEP_STARTED"] = payload => DeserializeEvent<WorkflowStepStartedEvent>(payload),
            ["STEP_FINISHED"] = payload => DeserializeEvent<WorkflowStepFinishedEvent>(payload),
            ["TEXT_MESSAGE_START"] = payload => DeserializeEvent<WorkflowTextMessageStartEvent>(payload),
            ["TEXT_MESSAGE_CONTENT"] = payload => DeserializeEvent<WorkflowTextMessageContentEvent>(payload),
            ["TEXT_MESSAGE_END"] = payload => DeserializeEvent<WorkflowTextMessageEndEvent>(payload),
            ["STATE_SNAPSHOT"] = payload => DeserializeEvent<WorkflowStateSnapshotEvent>(payload),
            ["TOOL_CALL_START"] = payload => DeserializeEvent<WorkflowToolCallStartEvent>(payload),
            ["TOOL_CALL_END"] = payload => DeserializeEvent<WorkflowToolCallEndEvent>(payload),
            ["CUSTOM"] = payload => DeserializeEvent<WorkflowCustomEvent>(payload),
        };

    public string GetEventType(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.Type;
    }

    public string Serialize(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);
    }

    public WorkflowRunEvent? Deserialize(string eventType, string payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(payload))
            return null;

        if (!Deserializers.TryGetValue(eventType, out var deserialize))
            return null;

        return deserialize(payload);
    }

    private static WorkflowRunEvent? DeserializeEvent<TEvent>(string payload)
        where TEvent : WorkflowRunEvent
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TEvent>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
