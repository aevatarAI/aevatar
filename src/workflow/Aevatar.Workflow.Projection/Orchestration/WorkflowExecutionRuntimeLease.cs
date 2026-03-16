using System.Runtime.ExceptionServices;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionOwnershipLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<WorkflowExecutionProjectionContext>,
      IProjectionRuntimeLeaseStopHandler
{
    private readonly CancellationTokenSource? _ownershipHeartbeatCts;
    private readonly Task? _ownershipHeartbeatTask;
    private readonly CancellationTokenSource? _projectionReleaseListenerCts;
    private readonly Task? _projectionReleaseListenerTask;
    private readonly TaskCompletionSource<bool> _projectionReleaseListenerReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IProjectionOwnershipCoordinator? _ownershipCoordinator;
    private readonly IProjectionSessionEventHub<WorkflowProjectionControlEvent>? _projectionControlHub;
    private readonly ILogger<WorkflowExecutionRuntimeLease> _logger;
    private int _ownershipHeartbeatStopped;
    private int _projectionReleaseListenerStopped;
    private int _projectionReleaseRequested;

    public WorkflowExecutionRuntimeLease(
        WorkflowExecutionProjectionContext context,
        IProjectionOwnershipCoordinator? ownershipCoordinator = null,
        ProjectionOwnershipCoordinatorOptions? ownershipOptions = null,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, WorkflowExecutionRuntimeLease>? lifecycle = null,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent>? projectionControlHub = null,
        ILogger<WorkflowExecutionRuntimeLease>? logger = null)
        : base(context.RootActorId)
    {
        Context = context;
        CommandId = context.SessionId;
        _ownershipCoordinator = ownershipCoordinator;
        _projectionControlHub = projectionControlHub;
        _logger = logger ?? NullLogger<WorkflowExecutionRuntimeLease>.Instance;

        if (ownershipCoordinator == null)
        {
            _ownershipHeartbeatCts = null;
            _ownershipHeartbeatTask = null;
        }
        else
        {
            _ownershipHeartbeatCts = new CancellationTokenSource();
            _ownershipHeartbeatTask = RunOwnershipHeartbeatAsync(
                ownershipCoordinator,
                ResolveHeartbeatInterval(ownershipOptions),
                _ownershipHeartbeatCts.Token);
        }

        if (lifecycle == null || projectionControlHub == null)
        {
            _projectionReleaseListenerCts = null;
            _projectionReleaseListenerTask = null;
            _projectionReleaseListenerReady.TrySetResult(true);
            return;
        }

        _projectionReleaseListenerCts = new CancellationTokenSource();
        _projectionReleaseListenerTask = RunProjectionReleaseListenerAsync(
            lifecycle,
            projectionControlHub,
            _projectionReleaseListenerCts.Token);
    }

    public string ActorId => RootEntityId;
    public string CommandId { get; }
    public WorkflowExecutionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;
    public string SessionId => CommandId;

    public async ValueTask StopOwnershipHeartbeatAsync()
    {
        if (_ownershipHeartbeatCts == null)
            return;

        if (Interlocked.Exchange(ref _ownershipHeartbeatStopped, 1) == 0)
            _ownershipHeartbeatCts.Cancel();

        try
        {
            if (_ownershipHeartbeatTask != null)
                await _ownershipHeartbeatTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected once the projection is being released.
        }
        finally
        {
            _ownershipHeartbeatCts.Dispose();
        }
    }

    public async ValueTask StopProjectionReleaseListenerAsync()
    {
        if (_projectionReleaseListenerCts == null)
            return;

        if (Interlocked.Exchange(ref _projectionReleaseListenerStopped, 1) == 0)
            _projectionReleaseListenerCts.Cancel();

        try
        {
            if (_projectionReleaseListenerTask != null)
                await _projectionReleaseListenerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected once the projection release listener is being stopped.
        }
        finally
        {
            _projectionReleaseListenerCts.Dispose();
        }
    }

    public Task WaitForProjectionReleaseListenerReadyAsync(CancellationToken ct = default) =>
        _projectionReleaseListenerReady.Task.WaitAsync(ct);

    private async Task RunOwnershipHeartbeatAsync(
        IProjectionOwnershipCoordinator ownershipCoordinator,
        TimeSpan interval,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ownershipCoordinator.AcquireAsync(ActorId, CommandId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Renewal is best-effort; keep trying until release stops the lease heartbeat.
            }
        }
    }

    private async Task RunProjectionReleaseListenerAsync(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, WorkflowExecutionRuntimeLease> lifecycle,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent> projectionControlHub,
        CancellationToken ct)
    {
        IAsyncDisposable? subscription = null;
        try
        {
            subscription = await projectionControlHub.SubscribeAsync(
                ActorId,
                CommandId,
                evt => HandleProjectionControlAsync(lifecycle, evt),
                ct).ConfigureAwait(false);
            _projectionReleaseListenerReady.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _projectionReleaseListenerReady.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _projectionReleaseListenerReady.TrySetException(ex);
            throw;
        }
        finally
        {
            if (subscription != null)
                await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask HandleProjectionControlAsync(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, WorkflowExecutionRuntimeLease> lifecycle,
        WorkflowProjectionControlEvent evt)
    {
        if (evt.EventCase != WorkflowProjectionControlEvent.EventOneofCase.ReleaseRequested)
            return;

        var releaseRequested = evt.ReleaseRequested;
        if (releaseRequested == null ||
            !string.Equals(releaseRequested.ActorId, ActorId, StringComparison.Ordinal) ||
            !string.Equals(releaseRequested.CommandId, CommandId, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.Exchange(ref _projectionReleaseRequested, 1) != 0)
            return;

        try
        {
            await lifecycle.StopAsync(this, CancellationToken.None).ConfigureAwait(false);
            await OnProjectionStoppedAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishReleaseCompletedAsync().ConfigureAwait(false);
            if (_projectionReleaseListenerCts != null &&
                Interlocked.Exchange(ref _projectionReleaseListenerStopped, 1) == 0)
            {
                _projectionReleaseListenerCts.Cancel();
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _projectionReleaseRequested, 0);
            _logger.LogWarning(
                ex,
                "Workflow projection release request handling failed. actorId={ActorId}, commandId={CommandId}",
                ActorId,
                CommandId);
        }
    }

    public async Task OnProjectionStoppedAsync(CancellationToken ct = default)
    {
        Exception? firstException = null;

        try
        {
            await StopOwnershipHeartbeatAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (_ownershipCoordinator != null)
        {
            try
            {
                await _ownershipCoordinator.ReleaseAsync(ActorId, CommandId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    private Task PublishReleaseCompletedAsync()
    {
        if (_projectionControlHub == null)
            return Task.CompletedTask;

        return _projectionControlHub.PublishAsync(
            ActorId,
            CommandId,
            new WorkflowProjectionControlEvent
            {
                ReleaseCompleted = new WorkflowProjectionReleaseCompletedEvent
                {
                    ActorId = ActorId,
                    CommandId = CommandId,
                },
            },
            CancellationToken.None);
    }

    private static TimeSpan ResolveHeartbeatInterval(ProjectionOwnershipCoordinatorOptions? ownershipOptions)
    {
        var leaseTtlMs = (ownershipOptions ?? new ProjectionOwnershipCoordinatorOptions()).ResolveLeaseTtlMs();
        var heartbeatIntervalMs = Math.Clamp(leaseTtlMs / 2, 500L, leaseTtlMs);
        return TimeSpan.FromMilliseconds(heartbeatIntervalMs);
    }
}
