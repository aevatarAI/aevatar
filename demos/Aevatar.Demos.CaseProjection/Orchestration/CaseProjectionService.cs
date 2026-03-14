using Aevatar.Demos.CaseProjection.Configuration;
using Aevatar.CQRS.Projection.Core.Abstractions;
using System.Collections.Concurrent;

namespace Aevatar.Demos.CaseProjection.Orchestration;

/// <summary>
/// Application-facing facade for case projection lifecycle and query.
/// </summary>
public sealed class CaseProjectionService : ICaseProjectionService
{
    private readonly CaseProjectionOptions _options;
    private readonly IProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>> _lifecycle;
    private readonly IProjectionDocumentReader<CaseProjectionReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;
    private readonly ICaseProjectionContextFactory _contextFactory;
    private readonly ConcurrentDictionary<string, CaseProjectionContext> _contexts = new(StringComparer.Ordinal);

    public CaseProjectionService(
        CaseProjectionOptions options,
        IProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>> lifecycle,
        IProjectionDocumentReader<CaseProjectionReadModel, string> documentReader,
        IProjectionClock clock,
        ICaseProjectionContextFactory contextFactory)
    {
        _options = options;
        _lifecycle = lifecycle;
        _documentReader = documentReader;
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
        var runId = Guid.NewGuid().ToString("N");
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
        _contexts[runId] = context;

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

    public async Task<CaseProjectionReadModel?> CompleteAsync(
        CaseProjectionSession session,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return null;

        _contexts.TryRemove(session.RunId, out _);
        await _lifecycle.CompleteAsync(session.Context, topology, ct);
        return await _documentReader.GetAsync(session.RunId, ct);
    }

    public async Task<IReadOnlyList<CaseProjectionReadModel>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return [];

        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = take,
            },
            ct);
        return result.Items;
    }

    public async Task<CaseProjectionReadModel?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return null;

        return await _documentReader.GetAsync(runId, ct);
    }
}
