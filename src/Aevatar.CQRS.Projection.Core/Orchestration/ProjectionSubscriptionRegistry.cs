using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic run-level projection registry built on top of shared actor stream subscriptions.
/// </summary>
public class ProjectionSubscriptionRegistry<TContext, TCompletion>
    : IProjectionSubscriptionRegistry<TContext>, IAsyncDisposable
    where TContext : IProjectionRunContext
{
    private readonly IProjectionCoordinator<TContext, TCompletion> _coordinator;
    private readonly IActorStreamSubscriptionHub<EventEnvelope> _subscriptionHub;
    private readonly IProjectionCompletionDetector<TContext> _completionDetector;
    private readonly ILogger<ProjectionSubscriptionRegistry<TContext, TCompletion>>? _logger;
    private readonly ConcurrentDictionary<string, ActiveRunState> _activeRunsByRunId = new(StringComparer.Ordinal);
    private int _disposed;

    public ProjectionSubscriptionRegistry(
        IProjectionCoordinator<TContext, TCompletion> coordinator,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub,
        IProjectionCompletionDetector<TContext> completionDetector,
        ILogger<ProjectionSubscriptionRegistry<TContext, TCompletion>>? logger = null)
    {
        _coordinator = coordinator;
        _subscriptionHub = subscriptionHub;
        _completionDetector = completionDetector;
        _logger = logger;
    }

    public async Task RegisterAsync(TContext context, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        var actorId = context.RootActorId;
        var runId = context.RunId;

        var envelopeSubscription = await _subscriptionHub.RegisterAsync(
            actorId,
            envelope => DispatchAsync(actorId, context, envelope),
            ct);

        var runState = new ActiveRunState(context, envelopeSubscription);
        if (!_activeRunsByRunId.TryAdd(runId, runState))
        {
            await envelopeSubscription.DisposeAsync();
            throw new InvalidOperationException($"Projection run already registered: '{runId}'.");
        }

        _logger?.LogDebug(
            "Registered projection run {RunId} for actor {ActorId}.",
            runId,
            actorId);
    }

    public async Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_activeRunsByRunId.TryRemove(runId, out var runState))
            return;

        runState.MarkProjectionStopped();
        await runState.Subscription.DisposeAsync();
    }

    public async Task<ProjectionRunCompletionStatus> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_activeRunsByRunId.TryGetValue(runId, out var runState))
            return ProjectionRunCompletionStatus.NotFound;

        if (runState.ProjectionCompletedTask.IsCompleted)
            return runState.Status;

        var timeoutTask = Task.Delay(timeout, ct);
        var completedTask = await Task.WhenAny(runState.ProjectionCompletedTask, timeoutTask);
        if (completedTask == timeoutTask)
        {
            ct.ThrowIfCancellationRequested();
            return ProjectionRunCompletionStatus.TimedOut;
        }

        _ = await runState.ProjectionCompletedTask;
        return runState.Status;
    }

    private async ValueTask DispatchAsync(string actorId, TContext context, EventEnvelope envelope)
    {
        if (!_activeRunsByRunId.TryGetValue(context.RunId, out var runState))
            return;
        if (!runState.IsProjectionActive)
            return;

        var isTerminal = _completionDetector.IsProjectionCompleted(context, envelope);
        try
        {
            await _coordinator.ProjectAsync(context, envelope, CancellationToken.None);
            if (isTerminal)
                runState.MarkProjectionCompleted();
        }
        catch (Exception ex)
        {
            runState.MarkProjectionFailed();
            _logger?.LogWarning(
                ex,
                "Projection dispatch failed for run {RunId} on actor {ActorId}.",
                context.RunId,
                actorId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        var runs = _activeRunsByRunId.Values.ToList();
        _activeRunsByRunId.Clear();

        foreach (var runState in runs)
        {
            runState.MarkProjectionStopped();
            try
            {
                await runState.Subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose projection run subscription.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class ActiveRunState
    {
        private readonly TaskCompletionSource<ProjectionRunCompletionStatus> _projectionCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _status;

        public ActiveRunState(
            TContext context,
            IAsyncDisposable subscription)
        {
            Context = context;
            Subscription = subscription;
        }

        public TContext Context { get; }
        public IAsyncDisposable Subscription { get; }

        public Task<ProjectionRunCompletionStatus> ProjectionCompletedTask => _projectionCompleted.Task;
        public bool IsProjectionActive => Volatile.Read(ref _status) == 0;
        public ProjectionRunCompletionStatus Status =>
            Volatile.Read(ref _status) switch
            {
                1 => ProjectionRunCompletionStatus.Completed,
                2 => ProjectionRunCompletionStatus.Failed,
                3 => ProjectionRunCompletionStatus.Stopped,
                _ => ProjectionRunCompletionStatus.TimedOut,
            };

        public void MarkProjectionCompleted()
        {
            if (Interlocked.CompareExchange(ref _status, 1, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(ProjectionRunCompletionStatus.Completed);
        }

        public void MarkProjectionFailed()
        {
            if (Interlocked.CompareExchange(ref _status, 2, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(ProjectionRunCompletionStatus.Failed);
        }

        public void MarkProjectionStopped()
        {
            if (Interlocked.CompareExchange(ref _status, 3, 0) != 0)
                return;

            _projectionCompleted.TrySetResult(ProjectionRunCompletionStatus.Stopped);
        }
    }
}
