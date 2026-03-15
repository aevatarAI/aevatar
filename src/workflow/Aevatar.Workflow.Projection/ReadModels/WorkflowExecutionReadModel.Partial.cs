using System.Collections.Generic;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Projection.ReadModels;

public enum WorkflowExecutionProjectionScope
{
    ActorShared = 0,
    RunIsolated = 1,
}

public enum WorkflowExecutionTopologySource
{
    RuntimeSnapshot = 0,
}

public enum WorkflowExecutionCompletionStatus
{
    Running = 0,
    Completed = 1,
    TimedOut = 2,
    Failed = 3,
    Stopped = 4,
    NotFound = 5,
    Disabled = 6,
    WaitingForSignal = 7,
    Unknown = 99,
}

public sealed partial class WorkflowExecutionReport
    : IProjectionReadModel,
      IProjectionReadModelCloneable<WorkflowExecutionReport>,
      IHasProjectionTimeline,
      IHasProjectionRoleReplies
{
    [JsonIgnore]
    public string ActorId => RootActorId;

    public DateTimeOffset CreatedAt
    {
        get => ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ToTimestamp(value);
    }

    public WorkflowExecutionProjectionScope ProjectionScope
    {
        get => (WorkflowExecutionProjectionScope)ProjectionScopeValue;
        set => ProjectionScopeValue = (int)value;
    }

    public WorkflowExecutionTopologySource TopologySource
    {
        get => (WorkflowExecutionTopologySource)TopologySourceValue;
        set => TopologySourceValue = (int)value;
    }

    public WorkflowExecutionCompletionStatus CompletionStatus
    {
        get => (WorkflowExecutionCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }

    public DateTimeOffset StartedAt
    {
        get => ToDateTimeOffset(StartedAtUtcValue);
        set => StartedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset EndedAt
    {
        get => ToDateTimeOffset(EndedAtUtcValue);
        set => EndedAtUtcValue = ToTimestamp(value);
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }

    public IList<WorkflowExecutionTopologyEdge> Topology
    {
        get => TopologyEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(TopologyEntries, value);
    }

    public IList<WorkflowExecutionStepTrace> Steps
    {
        get => StepEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(StepEntries, value);
    }

    public IList<WorkflowExecutionRoleReply> RoleReplies
    {
        get => RoleReplyEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(RoleReplyEntries, value);
    }

    public IList<WorkflowExecutionTimelineEvent> Timeline
    {
        get => TimelineEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(TimelineEntries, value);
    }

    public WorkflowExecutionSummary Summary
    {
        get => SummaryValue ??= new WorkflowExecutionSummary();
        set => SummaryValue = value ?? new WorkflowExecutionSummary();
    }

    public WorkflowExecutionReport DeepClone() => Clone();

    public void AddTimeline(ProjectionTimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);

        Timeline.Add(new WorkflowExecutionTimelineEvent
        {
            Timestamp = timelineEvent.Timestamp,
            Stage = timelineEvent.Stage,
            Message = timelineEvent.Message,
            AgentId = timelineEvent.AgentId,
            StepId = timelineEvent.StepId,
            StepType = timelineEvent.StepType,
            EventType = timelineEvent.EventType,
            Data = timelineEvent.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        });
    }

    public void AddRoleReply(ProjectionRoleReply roleReply)
    {
        ArgumentNullException.ThrowIfNull(roleReply);

        RoleReplies.Add(new WorkflowExecutionRoleReply
        {
            Timestamp = roleReply.Timestamp,
            RoleId = roleReply.RoleId,
            SessionId = roleReply.SessionId,
            Content = roleReply.Content,
            ContentLength = roleReply.ContentLength,
        });
    }

    private static Timestamp ToTimestamp(DateTimeOffset value) =>
        Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

}

public sealed partial class WorkflowExecutionCurrentStateDocument
    : IProjectionReadModel,
      IProjectionReadModelCloneable<WorkflowExecutionCurrentStateDocument>
{
    [JsonIgnore]
    public string ActorId => RootActorId;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }

    public WorkflowExecutionCurrentStateDocument DeepClone() => Clone();
}

public sealed partial class WorkflowExecutionSummary
{
    public IDictionary<string, int> StepTypeCounts
    {
        get => StepTypeCountsMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(StepTypeCountsMap, value);
    }
}

public sealed partial class WorkflowExecutionStepTrace
{
    public DateTimeOffset? RequestedAt
    {
        get => RequestedAtUtcValue == null ? null : RequestedAtUtcValue.ToDateTimeOffset();
        set => RequestedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public DateTimeOffset? CompletedAt
    {
        get => CompletedAtUtcValue == null ? null : CompletedAtUtcValue.ToDateTimeOffset();
        set => CompletedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }

    public IDictionary<string, string> RequestParameters
    {
        get => RequestParametersMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(RequestParametersMap, value);
    }

    public IDictionary<string, string> CompletionAnnotations
    {
        get => CompletionAnnotationsMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(CompletionAnnotationsMap, value);
    }

    public int? SuspensionTimeoutSeconds
    {
        get => SuspensionTimeoutSecondsValue == 0 ? null : SuspensionTimeoutSecondsValue;
        set => SuspensionTimeoutSecondsValue = value ?? 0;
    }

    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue
        ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
        : null;
}

public sealed partial class WorkflowExecutionRoleReply
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowExecutionTimelineEvent
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public IDictionary<string, string> Data
    {
        get => DataMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(DataMap, value);
    }
}

public sealed partial class WorkflowExecutionTopologyEdge
{
    public WorkflowExecutionTopologyEdge(string parent, string child)
    {
        Parent = parent ?? string.Empty;
        Child = child ?? string.Empty;
    }
}

internal static class WorkflowExecutionReadModelCollections
{
    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
            return;

        target.Add(source);
    }

    public static void ReplaceMap<TValue>(MapField<string, TValue> target, IDictionary<string, TValue>? source)
    {
        target.Clear();
        if (source == null)
            return;

        target.Add(source);
    }
}
