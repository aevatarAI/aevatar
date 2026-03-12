using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionRuntimeLease
    : ProjectionRuntimeLeaseBase<IEventSink<WorkflowRunEventEnvelope>>,
      IWorkflowExecutionProjectionLease,
      IProjectionPortSessionLease
{
    private readonly CancellationTokenSource? _ownershipHeartbeatCts;
    private readonly Task? _ownershipHeartbeatTask;
    private int _ownershipHeartbeatStopped;

    public WorkflowExecutionRuntimeLease(
        WorkflowExecutionProjectionContext context,
        IProjectionOwnershipCoordinator? ownershipCoordinator = null,
        ProjectionOwnershipCoordinatorOptions? ownershipOptions = null)
        : base(context.RootActorId)
    {
        Context = context;
        CommandId = context.CommandId;

        if (ownershipCoordinator == null)
            return;

        _ownershipHeartbeatCts = new CancellationTokenSource();
        _ownershipHeartbeatTask = RunOwnershipHeartbeatAsync(
            ownershipCoordinator,
            ResolveHeartbeatInterval(ownershipOptions),
            _ownershipHeartbeatCts.Token);
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

    private static TimeSpan ResolveHeartbeatInterval(ProjectionOwnershipCoordinatorOptions? ownershipOptions)
    {
        var leaseTtlMs = (ownershipOptions ?? new ProjectionOwnershipCoordinatorOptions()).ResolveLeaseTtlMs();
        var heartbeatIntervalMs = Math.Clamp(leaseTtlMs / 2, 500L, leaseTtlMs);
        return TimeSpan.FromMilliseconds(heartbeatIntervalMs);
    }
}
