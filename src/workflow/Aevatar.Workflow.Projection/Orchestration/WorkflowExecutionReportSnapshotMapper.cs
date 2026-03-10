using Aevatar.Workflow.Projection.Transport;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowExecutionReportSnapshotMapper
{
    public static Any Pack(WorkflowExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Any.Pack(ToSnapshot(report));
    }

    public static bool TryUnpack(Any? payload, out WorkflowExecutionReport? report)
    {
        report = null;
        if (payload == null || !payload.Is(WorkflowExecutionReportSnapshot.Descriptor))
            return false;

        try
        {
            report = FromSnapshot(payload.Unpack<WorkflowExecutionReportSnapshot>());
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            report = null;
            return false;
        }
    }

    private static WorkflowExecutionReportSnapshot ToSnapshot(WorkflowExecutionReport source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var snapshot = new WorkflowExecutionReportSnapshot
        {
            Id = source.Id,
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId ?? string.Empty,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(source.CreatedAt.ToUniversalTime()),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(source.UpdatedAt.ToUniversalTime()),
            RootActorId = source.RootActorId ?? string.Empty,
            CommandId = source.CommandId ?? string.Empty,
            ReportVersion = source.ReportVersion ?? string.Empty,
            ProjectionScope = (int)source.ProjectionScope,
            TopologySource = (int)source.TopologySource,
            CompletionStatus = (int)source.CompletionStatus,
            WorkflowName = source.WorkflowName ?? string.Empty,
            StartedAtUtc = Timestamp.FromDateTimeOffset(source.StartedAt.ToUniversalTime()),
            EndedAtUtc = Timestamp.FromDateTimeOffset(source.EndedAt.ToUniversalTime()),
            DurationMs = source.DurationMs,
            Success = source.Success,
            Input = source.Input ?? string.Empty,
            FinalOutput = source.FinalOutput ?? string.Empty,
            FinalError = source.FinalError ?? string.Empty,
            Summary = ToSummarySnapshot(source.Summary),
        };

        snapshot.Topology.Add((source.Topology ?? []).Select(x => new WorkflowExecutionTopologyEdgeSnapshot
        {
            Parent = x.Parent ?? string.Empty,
            Child = x.Child ?? string.Empty,
        }));
        snapshot.Steps.Add((source.Steps ?? []).Select(ToStepSnapshot));
        snapshot.RoleReplies.Add((source.RoleReplies ?? []).Select(ToRoleReplySnapshot));
        snapshot.Timeline.Add((source.Timeline ?? []).Select(ToTimelineSnapshot));
        return snapshot;
    }

    private static WorkflowExecutionReport FromSnapshot(WorkflowExecutionReportSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowExecutionReport
        {
            Id = source.Id,
            StateVersion = source.StateVersion,
            LastEventId = source.LastEventId ?? string.Empty,
            CreatedAt = ToDateTimeOffset(source.CreatedAtUtc),
            UpdatedAt = ToDateTimeOffset(source.UpdatedAtUtc),
            RootActorId = source.RootActorId ?? string.Empty,
            CommandId = source.CommandId ?? string.Empty,
            ReportVersion = source.ReportVersion ?? string.Empty,
            ProjectionScope = (WorkflowExecutionProjectionScope)source.ProjectionScope,
            TopologySource = (WorkflowExecutionTopologySource)source.TopologySource,
            CompletionStatus = (WorkflowExecutionCompletionStatus)source.CompletionStatus,
            WorkflowName = source.WorkflowName ?? string.Empty,
            StartedAt = ToDateTimeOffset(source.StartedAtUtc),
            EndedAt = ToDateTimeOffset(source.EndedAtUtc),
            DurationMs = source.DurationMs,
            Success = source.Success,
            Input = source.Input ?? string.Empty,
            FinalOutput = source.FinalOutput ?? string.Empty,
            FinalError = source.FinalError ?? string.Empty,
            Topology = source.Topology.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)).ToList(),
            Steps = source.Steps.Select(FromStepSnapshot).ToList(),
            RoleReplies = source.RoleReplies.Select(FromRoleReplySnapshot).ToList(),
            Timeline = source.Timeline.Select(FromTimelineSnapshot).ToList(),
            Summary = FromSummarySnapshot(source.Summary),
        };
    }

    private static WorkflowExecutionSummarySnapshot ToSummarySnapshot(WorkflowExecutionSummary? summary)
    {
        var source = summary ?? new WorkflowExecutionSummary();
        var snapshot = new WorkflowExecutionSummarySnapshot
        {
            TotalSteps = source.TotalSteps,
            RequestedSteps = source.RequestedSteps,
            CompletedSteps = source.CompletedSteps,
            RoleReplyCount = source.RoleReplyCount,
        };
        snapshot.StepTypeCounts.Add(source.StepTypeCounts ?? new Dictionary<string, int>(StringComparer.Ordinal));
        return snapshot;
    }

    private static WorkflowExecutionSummary FromSummarySnapshot(WorkflowExecutionSummarySnapshot? summary) =>
        new()
        {
            TotalSteps = summary?.TotalSteps ?? 0,
            RequestedSteps = summary?.RequestedSteps ?? 0,
            CompletedSteps = summary?.CompletedSteps ?? 0,
            RoleReplyCount = summary?.RoleReplyCount ?? 0,
            StepTypeCounts = summary?.StepTypeCounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ??
                new Dictionary<string, int>(StringComparer.Ordinal),
        };

    private static WorkflowExecutionStepTraceSnapshot ToStepSnapshot(WorkflowExecutionStepTrace source)
    {
        var snapshot = new WorkflowExecutionStepTraceSnapshot
        {
            StepId = source.StepId ?? string.Empty,
            StepType = source.StepType ?? string.Empty,
            TargetRole = source.TargetRole ?? string.Empty,
            RequestedAtUtc = ToNullableTimestamp(source.RequestedAt),
            CompletedAtUtc = ToNullableTimestamp(source.CompletedAt),
            Success = source.Success,
            WorkerId = source.WorkerId ?? string.Empty,
            OutputPreview = source.OutputPreview ?? string.Empty,
            Error = source.Error ?? string.Empty,
        };
        snapshot.RequestParameters.Add(source.RequestParameters ?? new Dictionary<string, string>(StringComparer.Ordinal));
        snapshot.CompletionMetadata.Add(source.CompletionMetadata ?? new Dictionary<string, string>(StringComparer.Ordinal));
        return snapshot;
    }

    private static WorkflowExecutionStepTrace FromStepSnapshot(WorkflowExecutionStepTraceSnapshot source) =>
        new()
        {
            StepId = source.StepId ?? string.Empty,
            StepType = source.StepType ?? string.Empty,
            TargetRole = source.TargetRole ?? string.Empty,
            RequestedAt = ToNullableDateTimeOffset(source.RequestedAtUtc),
            CompletedAt = ToNullableDateTimeOffset(source.CompletedAtUtc),
            Success = source.Success,
            WorkerId = source.WorkerId ?? string.Empty,
            OutputPreview = source.OutputPreview ?? string.Empty,
            Error = source.Error ?? string.Empty,
            RequestParameters = source.RequestParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            CompletionMetadata = source.CompletionMetadata.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        };

    private static WorkflowExecutionRoleReplySnapshot ToRoleReplySnapshot(WorkflowExecutionRoleReply source) =>
        new()
        {
            TimestampUtc = Timestamp.FromDateTimeOffset(source.Timestamp.ToUniversalTime()),
            RoleId = source.RoleId ?? string.Empty,
            SessionId = source.SessionId ?? string.Empty,
            Content = source.Content ?? string.Empty,
            ContentLength = source.ContentLength,
        };

    private static WorkflowExecutionRoleReply FromRoleReplySnapshot(WorkflowExecutionRoleReplySnapshot source) =>
        new()
        {
            Timestamp = ToDateTimeOffset(source.TimestampUtc),
            RoleId = source.RoleId ?? string.Empty,
            SessionId = source.SessionId ?? string.Empty,
            Content = source.Content ?? string.Empty,
            ContentLength = source.ContentLength,
        };

    private static WorkflowExecutionTimelineEventSnapshot ToTimelineSnapshot(WorkflowExecutionTimelineEvent source)
    {
        var snapshot = new WorkflowExecutionTimelineEventSnapshot
        {
            TimestampUtc = Timestamp.FromDateTimeOffset(source.Timestamp.ToUniversalTime()),
            Stage = source.Stage ?? string.Empty,
            Message = source.Message ?? string.Empty,
            AgentId = source.AgentId ?? string.Empty,
            StepId = source.StepId ?? string.Empty,
            StepType = source.StepType ?? string.Empty,
            EventType = source.EventType ?? string.Empty,
        };
        snapshot.Data.Add(source.Data ?? new Dictionary<string, string>(StringComparer.Ordinal));
        return snapshot;
    }

    private static WorkflowExecutionTimelineEvent FromTimelineSnapshot(WorkflowExecutionTimelineEventSnapshot source) =>
        new()
        {
            Timestamp = ToDateTimeOffset(source.TimestampUtc),
            Stage = source.Stage ?? string.Empty,
            Message = source.Message ?? string.Empty,
            AgentId = source.AgentId ?? string.Empty,
            StepId = source.StepId ?? string.Empty,
            StepType = source.StepType ?? string.Empty,
            EventType = source.EventType ?? string.Empty,
            Data = source.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        };

    private static Timestamp? ToNullableTimestamp(DateTimeOffset? value) =>
        value.HasValue
            ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime())
            : null;

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value?.ToDateTimeOffset() ?? default;

    private static DateTimeOffset? ToNullableDateTimeOffset(Timestamp? value) =>
        value == null
            ? null
            : value.ToDateTimeOffset();
}
