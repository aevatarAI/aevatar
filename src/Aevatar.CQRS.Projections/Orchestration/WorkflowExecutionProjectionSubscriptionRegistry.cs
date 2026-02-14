using Aevatar.Workflows.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Actor-level subscription registry that dispatches envelopes to active run contexts.
/// </summary>
public sealed class WorkflowExecutionProjectionSubscriptionRegistry : IWorkflowExecutionProjectionSubscriptionRegistry, IAsyncDisposable
{
    private static readonly string WorkflowCompletedTypeUrl =
        Google.Protobuf.WellKnownTypes.Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

    private readonly IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _coordinator;
    private readonly IStreamProvider _streams;
    private readonly ILogger<WorkflowExecutionProjectionSubscriptionRegistry>? _logger;
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _subscriptionsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ActiveRunState>> _activeRunsByActor = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunState> _activeRunsByRunId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _managementGate = new(1, 1);

    public WorkflowExecutionProjectionSubscriptionRegistry(
        IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> coordinator,
        IStreamProvider streams,
        ILogger<WorkflowExecutionProjectionSubscriptionRegistry>? logger = null)
    {
        _coordinator = coordinator;
        _streams = streams;
        _logger = logger;
    }

    public async Task RegisterAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        var runState = new ActiveRunState(context);
        var actorId = context.RootActorId;
        var registered = false;

        await _managementGate.WaitAsync(ct);
        try
        {
            var runs = _activeRunsByActor.GetOrAdd(actorId, _ =>
                new ConcurrentDictionary<string, ActiveRunState>(StringComparer.Ordinal));

            runs[context.RunId] = runState;
            _activeRunsByRunId[context.RunId] = runState;
            registered = true;

            if (_subscriptionsByActor.ContainsKey(actorId))
                return;

            try
            {
                var stream = _streams.GetStream(actorId);
                var subscription = await stream.SubscribeAsync<EventEnvelope>(
                    envelope => DispatchAsync(actorId, envelope),
                    CancellationToken.None);

                _subscriptionsByActor[actorId] = subscription;
            }
            catch
            {
                runs.TryRemove(context.RunId, out _);
                _activeRunsByRunId.TryRemove(context.RunId, out _);
                runState.MarkProjectionFailed();

                if (runs.IsEmpty)
                    _activeRunsByActor.TryRemove(actorId, out _);

                throw;
            }
        }
        finally
        {
            _managementGate.Release();
        }

        if (registered)
        {
            _logger?.LogDebug(
                "Registered projection run {RunId} for actor {ActorId}.",
                context.RunId,
                actorId);
        }
    }

    public async Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default)
    {
        IAsyncDisposable? subscriptionToDispose = null;
        ActiveRunState? removedRunState = null;

        await _managementGate.WaitAsync(ct);
        try
        {
            if (!_activeRunsByActor.TryGetValue(actorId, out var runs))
                return;

            if (runs.TryRemove(runId, out var state))
                removedRunState = state;

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

        removedRunState?.MarkProjectionStopped();

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
            if (!runState.IsProjectionActive)
                continue;

            try
            {
                await _coordinator.ProjectAsync(runState.Context, envelope, CancellationToken.None);
                if (isTerminal)
                    runState.MarkProjectionCompleted();
            }
            catch (Exception ex)
            {
                runState.MarkProjectionFailed();
                _logger?.LogWarning(
                    ex,
                    "Projection dispatch failed for run {RunId} on actor {ActorId}.",
                    runState.Context.RunId,
                    actorId);
            }
        }
    }

    private static bool IsProjectionTerminalEnvelope(EventEnvelope envelope) =>
        string.Equals(envelope.Payload?.TypeUrl, WorkflowCompletedTypeUrl, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable> subscriptions;
        await _managementGate.WaitAsync();
        try
        {
            foreach (var runState in _activeRunsByRunId.Values)
                runState.MarkProjectionStopped();

            subscriptions = _subscriptionsByActor.Values.ToList();
            _subscriptionsByActor.Clear();
            _activeRunsByActor.Clear();
            _activeRunsByRunId.Clear();
        }
        finally
        {
            _managementGate.Release();
            _managementGate.Dispose();
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                await subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose projection subscription.");
            }
        }
    }

    private sealed class ActiveRunState
    {
        private readonly TaskCompletionSource<bool> _projectionCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _status;

        public ActiveRunState(WorkflowExecutionProjectionContext context) => Context = context;

        public WorkflowExecutionProjectionContext Context { get; }

        public Task<bool> ProjectionCompletedTask => _projectionCompleted.Task;

        public bool IsProjectionActive => Volatile.Read(ref _status) == 0;

        public void MarkProjectionCompleted()
        {
            if (Interlocked.CompareExchange(ref _status, 1, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(true);
        }

        public void MarkProjectionFailed()
        {
            if (Interlocked.CompareExchange(ref _status, 2, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(false);
        }

        public void MarkProjectionStopped()
        {
            if (Interlocked.CompareExchange(ref _status, 3, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(false);
        }
    }
}
