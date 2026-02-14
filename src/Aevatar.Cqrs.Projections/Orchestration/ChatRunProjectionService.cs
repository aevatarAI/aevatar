using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Cqrs.Projections.Configuration;

namespace Aevatar.Cqrs.Projections.Orchestration;

/// <summary>
/// Application-facing facade for chat run projection lifecycle and read-model queries.
/// </summary>
public sealed class ChatRunProjectionService : IChatRunProjectionService
{
    private readonly ChatProjectionOptions _options;
    private readonly IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>> _coordinator;
    private readonly IProjectionReadModelStore<ChatRunReport, string> _store;
    private readonly IChatProjectionRunRegistry _runRegistry;

    public ChatRunProjectionService(
        ChatProjectionOptions options,
        IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>> coordinator,
        IProjectionReadModelStore<ChatRunReport, string> store,
        IChatProjectionRunRegistry runRegistry)
    {
        _options = options;
        _coordinator = coordinator;
        _store = store;
        _runRegistry = runRegistry;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableRunQueryEndpoints => _options.Enabled && _options.EnableRunQueryEndpoints;

    public bool EnableRunReportArtifacts => _options.Enabled && _options.EnableRunReportArtifacts;

    public async Task<ChatRunProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        if (!ProjectionEnabled)
        {
            return new ChatRunProjectionSession
            {
                RunId = runId,
                StartedAt = startedAt,
                Context = null,
            };
        }

        var context = new ChatProjectionContext
        {
            RunId = runId,
            RootActorId = rootActorId,
            WorkflowName = workflowName,
            StartedAt = startedAt,
            Input = input,
        };

        await _coordinator.InitializeAsync(context, ct);
        await _runRegistry.RegisterAsync(context, ct);

        return new ChatRunProjectionSession
        {
            RunId = runId,
            StartedAt = startedAt,
            Context = context,
        };
    }

    public Task ProjectAsync(
        ChatRunProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return Task.CompletedTask;

        return _coordinator.ProjectAsync(session.Context, envelope, ct);
    }

    public Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return Task.FromResult(false);

        var waitMs = Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs);
        return _runRegistry.WaitForCompletionAsync(runId, TimeSpan.FromMilliseconds(waitMs), ct);
    }

    public async Task<ChatRunReport?> CompleteAsync(
        ChatRunProjectionSession session,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return null;

        await _runRegistry.UnregisterAsync(session.Context.RootActorId, session.RunId, ct);
        await _coordinator.CompleteAsync(session.Context, topology, ct);
        return await _store.GetAsync(session.RunId, ct);
    }

    public async Task<IReadOnlyList<ChatRunReport>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return [];

        return await _store.ListAsync(take, ct);
    }

    public async Task<ChatRunReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return null;

        return await _store.GetAsync(runId, ct);
    }
}
