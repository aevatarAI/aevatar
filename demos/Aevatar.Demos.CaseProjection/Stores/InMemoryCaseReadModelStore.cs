namespace Aevatar.Demos.CaseProjection.Stores;

public sealed class InMemoryCaseReadModelStore : IProjectionDocumentStore<CaseProjectionReadModel, string>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CaseProjectionReadModel> _reports = new(StringComparer.Ordinal);

    public Task UpsertAsync(CaseProjectionReadModel readModel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            _reports[readModel.RunId] = Clone(readModel);
        return Task.CompletedTask;
    }

    public Task MutateAsync(string runId, Action<CaseProjectionReadModel> mutate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                throw new CaseReadModelNotFoundException(runId);

            mutate(report);
        }

        return Task.CompletedTask;
    }

    public Task<CaseProjectionReadModel?> GetAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_reports.TryGetValue(runId, out var report))
                return Task.FromResult<CaseProjectionReadModel?>(null);

            return Task.FromResult<CaseProjectionReadModel?>(Clone(report));
        }
    }

    public Task<IReadOnlyList<CaseProjectionReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 200);
        lock (_gate)
        {
            var list = _reports.Values
                .OrderByDescending(x => x.StartedAt)
                .Take(boundedTake)
                .Select(Clone)
                .ToList();

            return Task.FromResult<IReadOnlyList<CaseProjectionReadModel>>(list);
        }
    }

    private static CaseProjectionReadModel Clone(CaseProjectionReadModel source) => new()
    {
        ReadModelVersion = source.ReadModelVersion,
        RunId = source.RunId,
        RootActorId = source.RootActorId,
        CaseId = source.CaseId,
        CaseType = source.CaseType,
        Title = source.Title,
        Input = source.Input,
        Status = source.Status,
        OwnerId = source.OwnerId,
        EscalationLevel = source.EscalationLevel,
        StartedAt = source.StartedAt,
        EndedAt = source.EndedAt,
        Resolution = source.Resolution,
        Comments = source.Comments.Select(x => new CaseProjectionComment
        {
            Timestamp = x.Timestamp,
            AuthorId = x.AuthorId,
            Content = x.Content,
        }).ToList(),
        Timeline = source.Timeline.Select(x => new CaseProjectionTimelineItem
        {
            Timestamp = x.Timestamp,
            Stage = x.Stage,
            Message = x.Message,
            EventType = x.EventType,
            Data = x.Data.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal),
        }).ToList(),
        Topology = source.Topology.Select(x => new CaseTopologyEdge(x.Parent, x.Child)).ToList(),
        Summary = new CaseProjectionSummary
        {
            TotalEvents = source.Summary.TotalEvents,
            CommentCount = source.Summary.CommentCount,
            IsClosed = source.Summary.IsClosed,
            EscalationLevel = source.Summary.EscalationLevel,
        },
    };
}
