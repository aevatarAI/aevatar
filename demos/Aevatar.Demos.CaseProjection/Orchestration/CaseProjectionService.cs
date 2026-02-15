using Aevatar.Demos.CaseProjection.Configuration;

namespace Aevatar.Demos.CaseProjection.Orchestration;

/// <summary>
/// Application-facing facade for case projection lifecycle and query.
/// </summary>
public sealed class CaseProjectionService : ICaseProjectionService
{
    private readonly CaseProjectionOptions _options;
    private readonly IProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<CaseProjectionReadModel, string> _store;
    private readonly IProjectionRunIdGenerator _runIdGenerator;
    private readonly IProjectionClock _clock;
    private readonly ICaseProjectionContextFactory _contextFactory;

    public CaseProjectionService(
        CaseProjectionOptions options,
        IProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>> lifecycle,
        IProjectionReadModelStore<CaseProjectionReadModel, string> store,
        IProjectionRunIdGenerator runIdGenerator,
        IProjectionClock clock,
        ICaseProjectionContextFactory contextFactory)
    {
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
        _runIdGenerator = runIdGenerator;
        _clock = clock;
        _contextFactory = contextFactory;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableRunQueryEndpoints => _options.Enabled && _options.EnableRunQueryEndpoints;

    public bool EnableRunReportArtifacts => _options.Enabled && _options.EnableRunReportArtifacts;

    public async Task<CaseProjectionSession> StartAsync(
        string rootActorId,
        string caseId,
        string caseType,
        string input,
        CancellationToken ct = default)
    {
        var runId = _runIdGenerator.NextRunId();
        var startedAt = _clock.UtcNow;

        if (!ProjectionEnabled)
        {
            return new CaseProjectionSession
            {
                RunId = runId,
                StartedAt = startedAt,
                Context = null,
            };
        }

        var context = _contextFactory.Create(runId, rootActorId, caseId, caseType, input, startedAt);
        await _lifecycle.StartAsync(context, ct);

        return new CaseProjectionSession
        {
            RunId = runId,
            StartedAt = startedAt,
            Context = context,
        };
    }

    public Task ProjectAsync(
        CaseProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return Task.CompletedTask;

        return _lifecycle.ProjectAsync(session.Context, envelope, ct);
    }

    public Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return Task.FromResult(false);

        var waitMs = Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs);
        return _lifecycle.WaitForCompletionAsync(runId, TimeSpan.FromMilliseconds(waitMs), ct);
    }

    public async Task<CaseProjectionReadModel?> CompleteAsync(
        CaseProjectionSession session,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return null;

        await _lifecycle.CompleteAsync(session.Context, topology, ct);
        return await _store.GetAsync(session.RunId, ct);
    }

    public async Task<IReadOnlyList<CaseProjectionReadModel>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return [];

        return await _store.ListAsync(take, ct);
    }

    public async Task<CaseProjectionReadModel?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return null;

        return await _store.GetAsync(runId, ct);
    }
}
