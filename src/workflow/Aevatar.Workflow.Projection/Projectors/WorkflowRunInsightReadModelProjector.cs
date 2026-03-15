using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReadModelProjector
    : IProjectionProjector<WorkflowRunInsightProjectionContext, bool>
{
    private readonly IProjectionWriteDispatcher<WorkflowExecutionReport> _writeDispatcher;

    public WorkflowRunInsightReadModelProjector(IProjectionWriteDispatcher<WorkflowExecutionReport> writeDispatcher)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
    }

    public ValueTask InitializeAsync(WorkflowRunInsightProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        WorkflowRunInsightProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<WorkflowRunInsightState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var readModel = MapToReadModel(state);
        readModel.Id = state.RootActorId;
        readModel.RootActorId = state.RootActorId;
        readModel.StateVersion = stateEvent.Version;
        readModel.LastEventId = stateEvent.EventId ?? string.Empty;
        await _writeDispatcher.UpsertAsync(readModel, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowRunInsightProjectionContext context,
        bool completion,
        CancellationToken ct = default)
    {
        _ = context;
        _ = completion;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static WorkflowExecutionReport MapToReadModel(WorkflowRunInsightState source)
    {
        var readModel = new WorkflowExecutionReport
        {
            Id = source.RootActorId,
            RootActorId = source.RootActorId,
            CommandId = source.CommandId,
            ReportVersion = string.IsNullOrWhiteSpace(source.ReportVersion) ? "2.0" : source.ReportVersion,
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
            CompletionStatus = MapCompletionStatus(source.CompletionStatus),
            WorkflowName = source.WorkflowName ?? string.Empty,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            StartedAt = source.StartedAt,
            EndedAt = source.EndedAt,
            Success = source.Success,
            Input = source.Input ?? string.Empty,
            FinalOutput = source.FinalOutput ?? string.Empty,
            FinalError = source.FinalError ?? string.Empty,
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = source.SummaryValue?.TotalSteps ?? 0,
                RequestedSteps = source.SummaryValue?.RequestedSteps ?? 0,
                CompletedSteps = source.SummaryValue?.CompletedSteps ?? 0,
                RoleReplyCount = source.SummaryValue?.RoleReplyCount ?? 0,
                StepTypeCounts = source.SummaryValue?.StepTypeCountsMap.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase) ?? [],
            },
        };

        readModel.Topology = source.TopologyEntries
            .Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child))
            .ToList();
        readModel.Steps = source.StepEntries
            .Select(x => new WorkflowExecutionStepTrace
            {
                StepId = x.StepId,
                StepType = x.StepType,
                TargetRole = x.TargetRole,
                RequestedAt = x.RequestedAtUtcValue?.ToDateTimeOffset(),
                CompletedAt = x.CompletedAtUtcValue?.ToDateTimeOffset(),
                Success = x.SuccessWrapper,
                WorkerId = x.WorkerId,
                OutputPreview = x.OutputPreview,
                Error = x.Error,
                RequestParameters = x.RequestParametersMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                CompletionAnnotations = x.CompletionAnnotationsMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                NextStepId = x.NextStepId,
                BranchKey = x.BranchKey,
                AssignedVariable = x.AssignedVariable,
                AssignedValue = x.AssignedValue,
                SuspensionType = x.SuspensionType,
                SuspensionPrompt = x.SuspensionPrompt,
                SuspensionTimeoutSeconds = x.SuspensionTimeoutSecondsValue == 0 ? null : x.SuspensionTimeoutSecondsValue,
                RequestedVariableName = x.RequestedVariableName,
            })
            .ToList();
        readModel.RoleReplies = source.RoleReplyEntries
            .Select(x => new WorkflowExecutionRoleReply
            {
                Timestamp = x.TimestampUtcValue?.ToDateTimeOffset() ?? default,
                RoleId = x.RoleId,
                SessionId = x.SessionId,
                Content = x.Content,
                ContentLength = x.ContentLength,
            })
            .ToList();
        readModel.Timeline = source.TimelineEntries
            .Select(x => new WorkflowExecutionTimelineEvent
            {
                Timestamp = x.TimestampUtcValue?.ToDateTimeOffset() ?? default,
                Stage = x.Stage,
                Message = x.Message,
                AgentId = x.AgentId,
                StepId = x.StepId,
                StepType = x.StepType,
                EventType = x.EventType,
                Data = x.DataMap.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            })
            .ToList();

        return readModel;
    }

    private static WorkflowExecutionCompletionStatus MapCompletionStatus(WorkflowRunInsightCompletionStatus status) =>
        status switch
        {
            WorkflowRunInsightCompletionStatus.Completed => WorkflowExecutionCompletionStatus.Completed,
            WorkflowRunInsightCompletionStatus.TimedOut => WorkflowExecutionCompletionStatus.TimedOut,
            WorkflowRunInsightCompletionStatus.Failed => WorkflowExecutionCompletionStatus.Failed,
            WorkflowRunInsightCompletionStatus.Stopped => WorkflowExecutionCompletionStatus.Stopped,
            WorkflowRunInsightCompletionStatus.NotFound => WorkflowExecutionCompletionStatus.NotFound,
            WorkflowRunInsightCompletionStatus.Disabled => WorkflowExecutionCompletionStatus.Disabled,
            WorkflowRunInsightCompletionStatus.WaitingForSignal => WorkflowExecutionCompletionStatus.WaitingForSignal,
            _ => WorkflowExecutionCompletionStatus.Running,
        };
}
