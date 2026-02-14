using Aevatar.Cqrs.Projections.Abstractions.ReadModels;

namespace Aevatar.Cqrs.Projections.Stores;

/// <summary>
/// In-memory store for chat run read models.
/// </summary>
public sealed class InMemoryChatRunReadModelStore : IChatRunReadModelStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatRunReport> _reports = new(StringComparer.Ordinal);

    public Task UpsertAsync(ChatRunReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            _reports[report.RunId] = CloneReport(report);
        return Task.CompletedTask;
    }

    public Task MutateAsync(string runId, Action<ChatRunReport> mutate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                throw new ChatRunReadModelNotFoundException(runId);

            mutate(report);
        }

        return Task.CompletedTask;
    }

    public Task<ChatRunReport?> GetAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                return Task.FromResult<ChatRunReport?>(null);

            return Task.FromResult<ChatRunReport?>(CloneReport(report));
        }
    }

    public Task<IReadOnlyList<ChatRunReport>> ListAsync(int take = 50, CancellationToken ct = default)
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
            return Task.FromResult<IReadOnlyList<ChatRunReport>>(items);
        }
    }

    private static ChatRunReport CloneReport(ChatRunReport source) => new()
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
        Topology = source.Topology.Select(x => new ChatTopologyEdge(x.Parent, x.Child)).ToList(),
        Steps = source.Steps.Select(CloneStep).ToList(),
        RoleReplies = source.RoleReplies.Select(CloneRoleReply).ToList(),
        Timeline = source.Timeline.Select(CloneTimelineEvent).ToList(),
        Summary = CloneSummary(source.Summary),
    };

    private static ChatStepTrace CloneStep(ChatStepTrace source) => new()
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

    private static ChatRoleReply CloneRoleReply(ChatRoleReply source) => new()
    {
        Timestamp = source.Timestamp,
        RoleId = source.RoleId,
        SessionId = source.SessionId,
        Content = source.Content,
        ContentLength = source.ContentLength,
    };

    private static ChatTimelineEvent CloneTimelineEvent(ChatTimelineEvent source) => new()
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

    private static ChatRunSummary CloneSummary(ChatRunSummary source) => new()
    {
        TotalSteps = source.TotalSteps,
        RequestedSteps = source.RequestedSteps,
        CompletedSteps = source.CompletedSteps,
        RoleReplyCount = source.RoleReplyCount,
        StepTypeCounts = source.StepTypeCounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
    };
}
