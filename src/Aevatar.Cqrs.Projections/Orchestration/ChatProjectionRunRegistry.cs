using Aevatar.Workflows.Core;
using System.Collections.Concurrent;

namespace Aevatar.Cqrs.Projections.Orchestration;

/// <summary>
/// Actor-level subscription registry that dispatches envelopes to active run contexts.
/// </summary>
public sealed class ChatProjectionRunRegistry : IChatProjectionRunRegistry
{
    private static readonly string WorkflowCompletedTypeUrl =
        Google.Protobuf.WellKnownTypes.Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

    private readonly IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>> _coordinator;
    private readonly IStreamProvider _streams;
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _subscriptionsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ActiveRunState>> _activeRunsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunState> _activeRunsByRunId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _managementGate = new(1, 1);

    public ChatProjectionRunRegistry(
        IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>> coordinator,
        IStreamProvider streams)
    {
        _coordinator = coordinator;
        _streams = streams;
    }

    public async Task RegisterAsync(ChatProjectionContext context, CancellationToken ct = default)
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

    public async Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default)
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

    public async Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_activeRunsByRunId.TryGetValue(runId, out var runState))
            return false;

        if (runState.ProjectionCompletedTask.IsCompleted)
            return await runState.ProjectionCompletedTask;

        var timeoutTask = Task.Delay(timeout, ct);
        var completedTask = await Task.WhenAny(runState.ProjectionCompletedTask, timeoutTask);
        if (completedTask == timeoutTask)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        return await runState.ProjectionCompletedTask;
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
