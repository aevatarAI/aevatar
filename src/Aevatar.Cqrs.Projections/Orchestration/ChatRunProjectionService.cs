using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Cqrs.Projections.Configuration;
using Aevatar.Workflows.Core;
using System.Collections.Concurrent;

namespace Aevatar.Cqrs.Projections.Orchestration;

/// <summary>
/// Default facade for chat run projection lifecycle and read-model queries.
/// </summary>
public sealed class ChatRunProjectionService : IChatRunProjectionService
{
    private static readonly string WorkflowCompletedTypeUrl =
        Google.Protobuf.WellKnownTypes.Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

    private readonly ChatProjectionOptions _options;
    private readonly IChatProjectionCoordinator _coordinator;
    private readonly IChatRunReadModelStore _store;
    private readonly IStreamProvider _streams;
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _subscriptionsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ActiveRunState>> _activeRunsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunState> _activeRunsByRunId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _managementGate = new(1, 1);

    public ChatRunProjectionService(
        ChatProjectionOptions options,
        IChatProjectionCoordinator coordinator,
        IChatRunReadModelStore store,
        IStreamProvider streams)
    {
        _options = options;
        _coordinator = coordinator;
        _store = store;
        _streams = streams;
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
        await RegisterRunAsync(context, ct);

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

    public async Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return false;

        if (!_activeRunsByRunId.TryGetValue(runId, out var runState))
            return false;

        if (runState.ProjectionCompletedTask.IsCompleted)
            return await runState.ProjectionCompletedTask;

        var waitMs = Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs);
        var timeoutTask = Task.Delay(waitMs, ct);
        var completedTask = await Task.WhenAny(runState.ProjectionCompletedTask, timeoutTask);
        if (completedTask == timeoutTask)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        return await runState.ProjectionCompletedTask;
    }

    public async Task<ChatRunReport?> CompleteAsync(
        ChatRunProjectionSession session,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return null;

        await UnregisterRunAsync(session.Context.RootActorId, session.RunId, ct);
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

    private async Task RegisterRunAsync(ChatProjectionContext context, CancellationToken ct)
    {
        await _managementGate.WaitAsync(ct);
        try
        {
            var runs = _activeRunsByActor.GetOrAdd(context.RootActorId, _ =>
                new ConcurrentDictionary<string, ActiveRunState>(StringComparer.Ordinal));

            var runState = new ActiveRunState(context);
            runs[context.RunId] = runState;
            _activeRunsByRunId[context.RunId] = runState;

            if (_subscriptionsByActor.ContainsKey(context.RootActorId))
                return;

            var actorId = context.RootActorId;
            var stream = _streams.GetStream(actorId);
            var subscription = await stream.SubscribeAsync<EventEnvelope>(
                envelope => DispatchAsync(actorId, envelope),
                CancellationToken.None);

            _subscriptionsByActor[actorId] = subscription;
        }
        finally
        {
            _managementGate.Release();
        }
    }

    private async Task UnregisterRunAsync(string actorId, string runId, CancellationToken ct)
    {
        IAsyncDisposable? subscriptionToDispose = null;

        await _managementGate.WaitAsync(ct);
        try
        {
            if (!_activeRunsByActor.TryGetValue(actorId, out var runs))
                return;

            runs.TryRemove(runId, out _);
            _activeRunsByRunId.TryRemove(runId, out _);

            if (!runs.IsEmpty)
                return;

            _activeRunsByActor.TryRemove(actorId, out _);
            if (_subscriptionsByActor.TryRemove(actorId, out var subscription))
                subscriptionToDispose = subscription;
        }
        finally
        {
            _managementGate.Release();
        }

        if (subscriptionToDispose != null)
            await subscriptionToDispose.DisposeAsync();
    }

    private async Task DispatchAsync(string actorId, EventEnvelope envelope)
    {
        if (!_activeRunsByActor.TryGetValue(actorId, out var runs))
            return;

        var runStates = runs.Values.ToArray();
        var isTerminal = IsProjectionTerminalEnvelope(envelope);
        foreach (var runState in runStates)
        {
            try
            {
                await _coordinator.ProjectAsync(runState.Context, envelope, CancellationToken.None);
                if (isTerminal)
                    runState.MarkProjectionCompleted();
            }
            catch
            {
                // Keep dispatcher resilient: one run projection failure must not block others.
            }
        }
    }

    private static bool IsProjectionTerminalEnvelope(EventEnvelope envelope) =>
        string.Equals(envelope.Payload?.TypeUrl, WorkflowCompletedTypeUrl, StringComparison.Ordinal);

    private sealed class ActiveRunState
    {
        private readonly TaskCompletionSource<bool> _projectionCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveRunState(ChatProjectionContext context) => Context = context;

        public ChatProjectionContext Context { get; }

        public Task<bool> ProjectionCompletedTask => _projectionCompleted.Task;

        public void MarkProjectionCompleted() => _projectionCompleted.TrySetResult(true);
    }
}
