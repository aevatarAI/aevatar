using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeActivationService<TLease, TContext, TScopeAgent>
    : IProjectionScopeActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TContext : class, IProjectionMaterializationContext
    where TScopeAgent : IAgent
{
    private static readonly TimeSpan RelayReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelayReadinessCheckInterval = TimeSpan.FromMilliseconds(50);

    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<ProjectionScopeStartRequest, TContext> _contextFactory;
    private readonly Func<ProjectionRuntimeScopeKey, TContext, TLease> _leaseFactory;
    private readonly IStreamForwardingRegistry? _forwardingRegistry;

    public ProjectionScopeActivationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<ProjectionScopeStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory,
        IAgentTypeVerifier? agentTypeVerifier = null,
        IStreamPubSubMaintenance? streamPubSubMaintenance = null,
        ILoggerFactory? loggerFactory = null,
        IStreamForwardingRegistry? forwardingRegistry = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(
            runtime,
            dispatchPort,
            agentTypeVerifier,
            streamPubSubMaintenance,
            loggerFactory?.CreateLogger<ProjectionScopeActorRuntime<TScopeAgent>>());
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
        _forwardingRegistry = forwardingRegistry;
    }

    public async Task<TLease> EnsureAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
        // Refactor (iter41/cluster-041-command-observation-projection-activation):
        //   Old pattern: command observation binders ensure/activate projection/readmodel sessions before dispatch.
        //   New principle: observation binders attach only to existing projection-owned sessions;
        //   activation happens in projection-owned startup/background/committed-state lifecycle.
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var context = _contextFactory(request);
        var scopeKey = new ProjectionRuntimeScopeKey(
            context.RootActorId,
            context.ProjectionKind,
            request.Mode,
            request.SessionId);

        await _scopeRuntime.EnsureExistsAsync(scopeKey, ct).ConfigureAwait(false);
        await _scopeRuntime.DispatchAsync(
            scopeKey,
            new EnsureProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
                Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
            },
            ct).ConfigureAwait(false);
        await WaitForObservationRelayAsync(scopeKey, ct).ConfigureAwait(false);

        return _leaseFactory(scopeKey, context);
    }

    private async Task WaitForObservationRelayAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct)
    {
        if (_forwardingRegistry == null || string.IsNullOrWhiteSpace(scopeKey.RootActorId))
            return;

        var targetActorId = ProjectionScopeActorId.Build(scopeKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RelayReadinessTimeout);

        try
        {
            while (true)
            {
                var relays = await _forwardingRegistry
                    .ListBySourceAsync(scopeKey.RootActorId, timeout.Token)
                    .ConfigureAwait(false);
                if (relays.Any(relay => string.Equals(relay.TargetStreamId, targetActorId, StringComparison.Ordinal)))
                    return;

                await Task.Delay(RelayReadinessCheckInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for projection observation relay. root_actor_id={scopeKey.RootActorId} projection_kind={scopeKey.ProjectionKind} session_id={scopeKey.SessionId}");
        }
    }
}
