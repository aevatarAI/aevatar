using System.Text.Json;

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
            [WorkflowRunEventTypes.RunStarted] = payload => DeserializeEvent<WorkflowRunStartedEvent>(payload),
            [WorkflowRunEventTypes.RunFinished] = payload => DeserializeEvent<WorkflowRunFinishedEvent>(payload),
            [WorkflowRunEventTypes.RunError] = payload => DeserializeEvent<WorkflowRunErrorEvent>(payload),
            [WorkflowRunEventTypes.StepStarted] = payload => DeserializeEvent<WorkflowStepStartedEvent>(payload),
            [WorkflowRunEventTypes.StepFinished] = payload => DeserializeEvent<WorkflowStepFinishedEvent>(payload),
            [WorkflowRunEventTypes.TextMessageStart] = payload => DeserializeEvent<WorkflowTextMessageStartEvent>(payload),
            [WorkflowRunEventTypes.TextMessageContent] = payload => DeserializeEvent<WorkflowTextMessageContentEvent>(payload),
            [WorkflowRunEventTypes.TextMessageEnd] = payload => DeserializeEvent<WorkflowTextMessageEndEvent>(payload),
            [WorkflowRunEventTypes.StateSnapshot] = payload => DeserializeEvent<WorkflowStateSnapshotEvent>(payload),
            [WorkflowRunEventTypes.ToolCallStart] = payload => DeserializeEvent<WorkflowToolCallStartEvent>(payload),
            [WorkflowRunEventTypes.ToolCallEnd] = payload => DeserializeEvent<WorkflowToolCallEndEvent>(payload),
            [WorkflowRunEventTypes.Custom] = payload => DeserializeEvent<WorkflowCustomEvent>(payload),
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
