using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Reducers;

public sealed class StartWorkflowEventReducer : WorkflowExecutionEventReducerBase<StartWorkflowEvent>
{
    protected override bool Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        StartWorkflowEvent evt,
        DateTimeOffset now)
    {
        report.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? report.WorkflowName : evt.WorkflowName;
        var timelineData = BuildStartAuditData(evt);

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "workflow.start",
            $"command={report.CommandId}",
            envelope.PublisherId,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "",
            timelineData);

        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildStartAuditData(StartWorkflowEvent evt)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        AppendIfPresent(data, evt.Parameters, "session_id");
        AppendIfPresent(data, evt.Parameters, "channel_id");
        AppendIfPresent(data, evt.Parameters, "user_id");
        AppendIfPresent(data, evt.Parameters, "message_id");
        AppendIfPresent(data, evt.Parameters, "correlation_id");
        AppendIfPresent(data, evt.Parameters, "idempotency_key");
        AppendIfPresent(data, evt.Parameters, "workflow.session_id", "workflow_session_id");
        AppendIfPresent(data, evt.Parameters, "workflow.channel_id", "workflow_channel_id");
        AppendIfPresent(data, evt.Parameters, "workflow.user_id", "workflow_user_id");
        AppendIfPresent(data, evt.Parameters, "workflow.message_id", "workflow_message_id");
        AppendIfPresent(data, evt.Parameters, "workflow.correlation_id", "workflow_correlation_id");
        AppendIfPresent(data, evt.Parameters, "workflow.idempotency_key", "workflow_idempotency_key");
        return data;
    }

    private static void AppendIfPresent(
        IDictionary<string, string> target,
        Google.Protobuf.Collections.MapField<string, string> source,
        string sourceKey,
        string? targetKey = null)
    {
        if (!source.TryGetValue(sourceKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return;
        target[targetKey ?? sourceKey] = raw.Trim();
    }
}
