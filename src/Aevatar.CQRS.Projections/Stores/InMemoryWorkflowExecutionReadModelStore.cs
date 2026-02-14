using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Stores;

/// <summary>
/// In-memory store for chat run read models.
/// </summary>
public sealed class InMemoryWorkflowExecutionReadModelStore : IWorkflowExecutionReadModelStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkflowExecutionReport> _reports = new(StringComparer.Ordinal);

    public Task UpsertAsync(WorkflowExecutionReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            _reports[report.RunId] = CloneReport(report);
        return Task.CompletedTask;
    }

    public Task MutateAsync(string runId, Action<WorkflowExecutionReport> mutate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                throw new WorkflowExecutionReadModelNotFoundException(runId);

            mutate(report);
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowExecutionReport?> GetAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                return Task.FromResult<WorkflowExecutionReport?>(null);

            return Task.FromResult<WorkflowExecutionReport?>(CloneReport(report));
        }
    }

    public Task<IReadOnlyList<WorkflowExecutionReport>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 200);
        lock (_gate)
        {
            var items = _reports.Values
                .OrderByDescending(x => x.StartedAt)
                .Take(boundedTake)
                .Select(CloneReport)
                .ToList();
            return Task.FromResult<IReadOnlyList<WorkflowExecutionReport>>(items);
        }
    }

    private static WorkflowExecutionReport CloneReport(WorkflowExecutionReport source) => new()
    {
        ReportVersion = source.ReportVersion,
        WorkflowName = source.WorkflowName,
        RootActorId = source.RootActorId,
        RunId = source.RunId,
        StartedAt = source.StartedAt,
        EndedAt = source.EndedAt,
        DurationMs = source.DurationMs,
        Success = source.Success,
        Input = source.Input,
        FinalOutput = source.FinalOutput,
        FinalError = source.FinalError,
        Topology = source.Topology.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)).ToList(),
        Steps = source.Steps.Select(CloneStep).ToList(),
        RoleReplies = source.RoleReplies.Select(CloneRoleReply).ToList(),
        Timeline = source.Timeline.Select(CloneTimelineEvent).ToList(),
        Summary = CloneSummary(source.Summary),
    };

    private static WorkflowExecutionStepTrace CloneStep(WorkflowExecutionStepTrace source) => new()
    {
        StepId = source.StepId,
        StepType = source.StepType,
        RunId = source.RunId,
        TargetRole = source.TargetRole,
        RequestedAt = source.RequestedAt,
        CompletedAt = source.CompletedAt,
        Success = source.Success,
        WorkerId = source.WorkerId,
        OutputPreview = source.OutputPreview,
        Error = source.Error,
        RequestParameters = source.RequestParameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        CompletionMetadata = source.CompletionMetadata.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
    };

    private static WorkflowExecutionRoleReply CloneRoleReply(WorkflowExecutionRoleReply source) => new()
    {
        Timestamp = source.Timestamp,
        RoleId = source.RoleId,
        SessionId = source.SessionId,
        Content = source.Content,
        ContentLength = source.ContentLength,
    };

    private static WorkflowExecutionTimelineEvent CloneTimelineEvent(WorkflowExecutionTimelineEvent source) => new()
    {
        Timestamp = source.Timestamp,
        Stage = source.Stage,
        Message = source.Message,
        AgentId = source.AgentId,
        StepId = source.StepId,
        StepType = source.StepType,
        EventType = source.EventType,
        Data = source.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
    };

    private static WorkflowExecutionSummary CloneSummary(WorkflowExecutionSummary source) => new()
    {
        TotalSteps = source.TotalSteps,
        RequestedSteps = source.RequestedSteps,
        CompletedSteps = source.CompletedSteps,
        RoleReplyCount = source.RoleReplyCount,
        StepTypeCounts = source.StepTypeCounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
    };
}
