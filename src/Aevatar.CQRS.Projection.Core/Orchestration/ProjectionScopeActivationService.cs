using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aevatar.CQRS.Projection.Core.Observability;

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
    private readonly IStreamForwardingBindingAuthority? _bindingAuthority;
    private readonly ILogger<ProjectionScopeActivationService<TLease, TContext, TScopeAgent>> _logger;

    public ProjectionScopeActivationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<ProjectionScopeStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory,
        IAgentKindVerifier? agentKindVerifier = null,
        IAgentKindRegistry? agentKindRegistry = null,
        IStreamPubSubMaintenance? streamPubSubMaintenance = null,
        ILoggerFactory? loggerFactory = null,
        IStreamForwardingBindingAuthority? bindingAuthority = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(
            runtime,
            dispatchPort,
            agentKindVerifier,
            agentKindRegistry,
            streamPubSubMaintenance,
            loggerFactory?.CreateLogger<ProjectionScopeActorRuntime<TScopeAgent>>());
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
        _bindingAuthority = bindingAuthority;
        _logger = loggerFactory?.CreateLogger<ProjectionScopeActivationService<TLease, TContext, TScopeAgent>>() ??
                  NullLogger<ProjectionScopeActivationService<TLease, TContext, TScopeAgent>>.Instance;
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

        var targetActorId = ProjectionScopeActorId.Build(scopeKey);
        if (await HasExactActivationEvidenceAsync(scopeKey, targetActorId, ct).ConfigureAwait(false))
        {
            ProjectionActivationMetrics.RecordResult("warm", scopeKey.Mode, "success");
            return _leaseFactory(scopeKey, context);
        }

        var ensureDispatched = false;
        try
        {
            await _scopeRuntime.EnsureExistsAsync(scopeKey, ct).ConfigureAwait(false);
            await DispatchEnsureAsync(scopeKey, ct).ConfigureAwait(false);
            ensureDispatched = true;
            await WaitForObservationRelayAsync(scopeKey, targetActorId, ct).ConfigureAwait(false);
            ProjectionActivationMetrics.RecordResult("cold", scopeKey.Mode, "success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordResult("cold", scopeKey.Mode, "cancelled");
            if (ensureDispatched && scopeKey.Mode == ProjectionRuntimeMode.SessionObservation)
                await ReleaseFailedActivationAsync(scopeKey).ConfigureAwait(false);
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordResult("cold", scopeKey.Mode, "failure");
            // Durable scopes outlive an activation attempt. A late ensure must retain its relay
            // so the same unconfirmed committed publication can recover after the backlog drains.
            if (ensureDispatched && scopeKey.Mode == ProjectionRuntimeMode.SessionObservation)
                await ReleaseFailedActivationAsync(scopeKey).ConfigureAwait(false);
            throw;
        }

        return _leaseFactory(scopeKey, context);
    }

    private async Task DispatchEnsureAsync(ProjectionRuntimeScopeKey scopeKey, CancellationToken ct)
    {
        var startedAt = ProjectionActivationMetrics.StartTimestamp();
        try
        {
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
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.DispatchAdmissionStage,
                startedAt,
                scopeKey.Mode,
                "success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.DispatchAdmissionStage,
                startedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.DispatchAdmissionStage,
                startedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }
    }

    private async Task ReleaseFailedActivationAsync(ProjectionRuntimeScopeKey scopeKey)
    {
        try
        {
            await _scopeRuntime.DispatchAsync(
                scopeKey,
                new ReleaseProjectionScopeCommand
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    SessionId = scopeKey.SessionId,
                    Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Projection scope activation compensation dispatch failed. actorId={ActorId} projectionKind={ProjectionKind}",
                ProjectionScopeActorId.Build(scopeKey),
                scopeKey.ProjectionKind);
        }
    }

    private async Task<bool> HasExactActivationEvidenceAsync(
        ProjectionRuntimeScopeKey scopeKey,
        string targetActorId,
        CancellationToken ct)
    {
        var startedAt = ProjectionActivationMetrics.StartTimestamp();
        try
        {
            var binding = await RequireBindingAuthority()
                .GetAsync(scopeKey.RootActorId, targetActorId, ct)
                .ConfigureAwait(false);
            var matches = ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                binding,
                scopeKey.RootActorId,
                targetActorId,
                _scopeRuntime.ScopeAgentKind);
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.AuthorityLookupStage,
                startedAt,
                scopeKey.Mode,
                matches ? "hit" : "miss");
            return matches;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.AuthorityLookupStage,
                startedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.AuthorityLookupStage,
                startedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }
    }

    private async Task WaitForObservationRelayAsync(
        ProjectionRuntimeScopeKey scopeKey,
        string targetActorId,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RelayReadinessTimeout);
        var startedAt = ProjectionActivationMetrics.StartTimestamp();

        try
        {
            while (true)
            {
                var binding = await RequireBindingAuthority()
                    .GetAsync(scopeKey.RootActorId, targetActorId, timeout.Token)
                    .ConfigureAwait(false);
                if (ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                        binding,
                        scopeKey.RootActorId,
                        targetActorId,
                        _scopeRuntime.ScopeAgentKind))
                {
                    ProjectionActivationMetrics.RecordStage(
                        ProjectionActivationMetrics.RelayReadinessStage,
                        startedAt,
                        scopeKey.Mode,
                        "success");
                    return;
                }

                await Task.Delay(RelayReadinessCheckInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.RelayReadinessStage,
                startedAt,
                scopeKey.Mode,
                "timeout");
            throw new TimeoutException(
                $"Timed out waiting for projection observation relay. root_actor_id={scopeKey.RootActorId} projection_kind={scopeKey.ProjectionKind} session_id={scopeKey.SessionId}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.RelayReadinessStage,
                startedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.RelayReadinessStage,
                startedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }
    }

    private IStreamForwardingBindingAuthority RequireBindingAuthority() =>
        _bindingAuthority ?? throw new InvalidOperationException(
            "IStreamForwardingBindingAuthority is required to prove projection relay readiness.");
}
