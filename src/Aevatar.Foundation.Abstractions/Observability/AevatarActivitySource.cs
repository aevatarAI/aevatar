// ─────────────────────────────────────────────────────────────
// AevatarActivitySource - distributed tracing ActivitySource.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Globalization;
using Aevatar.Foundation.Abstractions.Propagation;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>ActivitySource for Aevatar agent distributed tracing.</summary>
public static class AevatarActivitySource
{
    private const string UnknownAgentType = "unknown";

    public const string ActivitySourceName = "Aevatar.Agents";

    public const string AgentIdTag = "aevatar.agent.id";
    public const string AgentTypeTag = "aevatar.agent.type";
    public const string AgentParentTag = "aevatar.agent.parent";
    public const string EventIdTag = "aevatar.event.id";
    public const string EventTypeTag = "aevatar.event.type";
    public const string EventDirectionTag = "aevatar.event.direction";
    public const string EventPublisherTag = "aevatar.event.publisher";
    public const string ProjectionNameTag = "aevatar.projection.name";
    public const string ProjectionStateVersionTag = "aevatar.projection.state.version";
    public const string ProjectionLastEventIdTag = "aevatar.projection.last_event_id";
    public const string ReadModelNameTag = "aevatar.readmodel.name";
    public const string ReadModelStateVersionTag = "aevatar.readmodel.state.version";
    public const string ReadModelIdTag = "aevatar.readmodel.id";
    public const string WorkflowRunIdTag = "aevatar.workflow.run_id";
    public const string WorkflowNameTag = "aevatar.workflow.name";
    public const string WorkflowStepTag = "aevatar.workflow.step";

    public const string AgentSpawnActivityName = "aevatar.agent.spawn";
    public const string AgentDeactivateActivityName = "aevatar.agent.deactivate";
    public const string AgentLinkActivityName = "aevatar.agent.link";
    public const string AgentUnlinkActivityName = "aevatar.agent.unlink";
    public const string ProjectionMaterializeActivityName = "aevatar.projection.materialize";
    public const string ReadModelUpsertActivityName = "aevatar.readmodel.upsert";
    public const string ReadModelDeleteActivityName = "aevatar.readmodel.delete";
    public const string WorkflowRunActivityName = "aevatar.workflow.run";

    /// <summary>ActivitySource instance.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>Starts a HandleEvent activity (legacy, used by LocalActor).</summary>
    public static Activity? StartHandleEvent(
        string agentId,
        string eventId,
        string? eventTypeUrl = null,
        string? agentType = null)
    {
        var eventTypeName = ResolveEventTypeName(eventTypeUrl);
        var operationName = BuildHandleEventOperationName(eventTypeName);

        var activity = TryStartActivity(operationName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentIdTag, agentId);
        SafeSetTag(activity, AgentTypeTag, ResolveAgentTypeTag(agentType));
        SafeSetTag(activity, EventIdTag, eventId);
        if (!string.IsNullOrWhiteSpace(eventTypeUrl))
            SafeSetTag(activity, EventTypeTag, eventTypeUrl);
        else if (!string.IsNullOrWhiteSpace(eventTypeName))
            SafeSetTag(activity, EventTypeTag, eventTypeName);

        return activity;
    }

    public static Activity? StartHandleEvent(
        string agentId,
        EventEnvelope envelope,
        string? agentType = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var eventTypeUrl = envelope.Payload?.TypeUrl;
        var eventTypeName = ResolveEventTypeName(eventTypeUrl);
        var operationName = BuildHandleEventOperationName(eventTypeName);

        var activity = TryStartWithEnvelopeParent(operationName, envelope) ?? TryStartActivity(operationName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentIdTag, agentId);
        SafeSetTag(activity, AgentTypeTag, ResolveAgentTypeTag(agentType));
        SafeSetTag(activity, EventIdTag, envelope.Id);
        if (!string.IsNullOrWhiteSpace(eventTypeUrl))
            SafeSetTag(activity, EventTypeTag, eventTypeUrl);
        else if (!string.IsNullOrWhiteSpace(eventTypeName))
            SafeSetTag(activity, EventTypeTag, eventTypeName);
        SafeSetTag(activity, EventDirectionTag, envelope.Route.Describe());
        if (!string.IsNullOrWhiteSpace(envelope.Route?.PublisherActorId))
            SafeSetTag(activity, EventPublisherTag, envelope.Route.PublisherActorId);

        return activity;
    }

    public static Activity? StartAgentSpawn(string agentId, string agentType)
    {
        var activity = TryStartActivity(AgentSpawnActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentIdTag, agentId);
        SafeSetTag(activity, AgentTypeTag, ResolveAgentTypeTag(agentType));
        return activity;
    }

    public static Activity? StartAgentDeactivate(string agentId, string? agentType = null)
    {
        var activity = TryStartActivity(AgentDeactivateActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentIdTag, agentId);
        if (!string.IsNullOrWhiteSpace(agentType))
            SafeSetTag(activity, AgentTypeTag, agentType);
        return activity;
    }

    public static Activity? StartAgentLink(string parentId, string childId)
    {
        var activity = TryStartActivity(AgentLinkActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentParentTag, parentId);
        SafeSetTag(activity, AgentIdTag, childId);
        return activity;
    }

    public static Activity? StartAgentUnlink(string parentId, string childId)
    {
        var activity = TryStartActivity(AgentUnlinkActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, AgentParentTag, parentId);
        SafeSetTag(activity, AgentIdTag, childId);
        return activity;
    }

    public static Activity? StartProjectionMaterialize(string projectionName, string lastEventId)
    {
        var activity = TryStartActivity(ProjectionMaterializeActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, ProjectionNameTag, projectionName);
        SafeSetTag(activity, ProjectionLastEventIdTag, lastEventId);
        return activity;
    }

    public static Activity? StartReadmodelUpsert(string readmodelName, long stateVersion)
    {
        var activity = TryStartActivity(ReadModelUpsertActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, ReadModelNameTag, readmodelName);
        SafeSetTag(activity, ReadModelStateVersionTag, stateVersion);
        return activity;
    }

    public static Activity? StartReadmodelDelete(string readmodelName, string id)
    {
        var activity = TryStartActivity(ReadModelDeleteActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, ReadModelNameTag, readmodelName);
        SafeSetTag(activity, ReadModelIdTag, id);
        return activity;
    }

    public static Activity? StartWorkflowRun(string runId, string workflowName, string? step = null)
    {
        var activity = TryStartActivity(WorkflowRunActivityName);
        if (activity == null)
            return null;

        SafeSetTag(activity, WorkflowRunIdTag, runId);
        SafeSetTag(activity, WorkflowNameTag, workflowName);
        if (!string.IsNullOrWhiteSpace(step))
            SafeSetTag(activity, WorkflowStepTag, step);
        return activity;
    }

    /// <summary>Starts an invoke_agent span per GenAI semantic conventions.</summary>
    public static Activity? StartInvokeAgent(string agentId, string? agentName = null, string? system = null)
    {
        var activity = TryStartActivity($"invoke_agent {agentName ?? agentId}", ActivityKind.Client);
        if (activity == null) return null;

        SafeSetTag(activity, "gen_ai.operation.name", "invoke_agent");
        SafeSetTag(activity, "gen_ai.agent.id", agentId);
        if (agentName != null) SafeSetTag(activity, "gen_ai.agent.name", agentName);
        if (system != null) SafeSetTag(activity, "gen_ai.system", system);

        return activity;
    }

    /// <summary>Starts a chat span per GenAI semantic conventions.</summary>
    public static Activity? StartChat(string? model = null, string? system = null)
    {
        var activity = TryStartActivity($"chat {model ?? "unknown"}", ActivityKind.Client);
        if (activity == null) return null;

        SafeSetTag(activity, "gen_ai.operation.name", "chat");
        if (model != null) SafeSetTag(activity, "gen_ai.request.model", model);
        if (system != null) SafeSetTag(activity, "gen_ai.system", system);

        return activity;
    }

    /// <summary>Records token usage on a chat span.</summary>
    public static void RecordTokenUsage(Activity? activity, int? inputTokens, int? outputTokens)
    {
        if (activity == null) return;
        if (inputTokens.HasValue) SafeSetTag(activity, "gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue) SafeSetTag(activity, "gen_ai.usage.output_tokens", outputTokens.Value);
    }

    /// <summary>Starts an execute_tool span per GenAI semantic conventions.</summary>
    public static Activity? StartExecuteTool(string toolName, string? toolCallId = null)
    {
        var activity = TryStartActivity($"execute_tool {toolName}", ActivityKind.Client);
        if (activity == null) return null;

        SafeSetTag(activity, "gen_ai.operation.name", "execute_tool");
        SafeSetTag(activity, "gen_ai.tool.name", toolName);
        if (toolCallId != null) SafeSetTag(activity, "gen_ai.tool.call_id", toolCallId);

        return activity;
    }

    public static void SafeSetTag(Activity? activity, string key, object? value)
    {
        if (activity == null)
            return;

        try
        {
            activity.SetTag(key, value);
        }
        catch
        {
            // Observability must never affect the business path.
        }
    }

    public static void SafeSetStatus(Activity? activity, ActivityStatusCode statusCode, string? description = null)
    {
        if (activity == null)
            return;

        try
        {
            activity.SetStatus(statusCode, description);
        }
        catch
        {
            // Observability must never affect the business path.
        }
    }

    private static Activity? TryStartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        try
        {
            return Source.StartActivity(operationName, kind);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildHandleEventOperationName(string eventTypeName)
    {
        return string.IsNullOrWhiteSpace(eventTypeName)
            ? "HandleEvent:UnknownEvent"
            : $"HandleEvent:{eventTypeName}";
    }

    private static string ResolveAgentTypeTag(string? agentType)
    {
        return string.IsNullOrWhiteSpace(agentType) ? UnknownAgentType : agentType;
    }

    private static string ResolveEventTypeName(string? eventTypeUrl)
    {
        if (string.IsNullOrWhiteSpace(eventTypeUrl))
            return string.Empty;

        var separator = eventTypeUrl.LastIndexOf('/');
        var fullTypeName = separator < 0 || separator == eventTypeUrl.Length - 1
            ? eventTypeUrl
            : eventTypeUrl[(separator + 1)..];

        var dot = fullTypeName.LastIndexOf('.');
        if (dot < 0 || dot == fullTypeName.Length - 1)
            return fullTypeName;

        return fullTypeName[(dot + 1)..];
    }

    private static Activity? TryStartWithEnvelopeParent(string operationName, EventEnvelope envelope)
    {
        var trace = envelope.Propagation?.Trace;
        if (trace == null ||
            string.IsNullOrWhiteSpace(trace.TraceId) ||
            string.IsNullOrWhiteSpace(trace.SpanId))
        {
            return null;
        }

        try
        {
            var traceId = ActivityTraceId.CreateFromString(trace.TraceId.AsSpan());
            var spanId = ActivitySpanId.CreateFromString(trace.SpanId.AsSpan());
            var traceFlags = ResolveTraceFlags(trace.TraceFlags);
            var parent = new ActivityContext(traceId, spanId, traceFlags);
            return Source.StartActivity(operationName, ActivityKind.Internal, parent);
        }
        catch
        {
            return null;
        }
    }

    private static ActivityTraceFlags ResolveTraceFlags(string? flagsText)
    {
        if (string.IsNullOrWhiteSpace(flagsText))
        {
            return ActivityTraceFlags.None;
        }

        if (byte.TryParse(flagsText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flagsByte))
            return (ActivityTraceFlags)flagsByte;

        return Enum.TryParse<ActivityTraceFlags>(flagsText, ignoreCase: true, out var parsed)
            ? parsed
            : ActivityTraceFlags.None;
    }
}
