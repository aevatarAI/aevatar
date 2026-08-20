using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.CQRS.Projection.Core.Observability;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeReleaseService<TLease, TScopeAgent>
    : IProjectionScopeReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TScopeAgent : IAgent
{
    private static readonly TimeSpan RelayReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelayReleaseCheckInterval = TimeSpan.FromMilliseconds(50);

    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<TLease, ProjectionRuntimeScopeKey> _scopeKeyAccessor;
    private readonly IStreamForwardingBindingAuthority? _bindingAuthority;
    private readonly IStreamForwardingRegistry? _forwardingRegistry;

    public ProjectionScopeReleaseService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<TLease, ProjectionRuntimeScopeKey> scopeKeyAccessor,
        IAgentKindVerifier? agentKindVerifier = null,
        IAgentKindRegistry? agentKindRegistry = null,
        IStreamForwardingBindingAuthority? bindingAuthority = null,
        IStreamForwardingRegistry? forwardingRegistry = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(
            runtime,
            dispatchPort,
            agentKindVerifier,
            agentKindRegistry);
        _scopeKeyAccessor = scopeKeyAccessor ?? throw new ArgumentNullException(nameof(scopeKeyAccessor));
        _bindingAuthority = bindingAuthority;
        _forwardingRegistry = forwardingRegistry;
    }

    public async Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        var scopeKey = _scopeKeyAccessor(lease);
        var targetActorId = ProjectionScopeActorId.Build(scopeKey);
        var authority = RequireBindingAuthority();
        var binding = await authority
            .GetAsync(scopeKey.RootActorId, targetActorId, ct)
            .ConfigureAwait(false);
        if (!await _scopeRuntime.ExistsAsync(scopeKey, ct).ConfigureAwait(false))
        {
            if (binding == null)
                return;

            var registry = _forwardingRegistry ?? throw new InvalidOperationException(
                "IStreamForwardingRegistry is required to remove stale projection relays.");
            await registry.RemoveAsync(scopeKey.RootActorId, targetActorId, ct).ConfigureAwait(false);
            await WaitForObservationRelayRemovalAsync(scopeKey, targetActorId, ct).ConfigureAwait(false);
            return;
        }

        await _scopeRuntime.DispatchAsync(
            scopeKey,
            new ReleaseProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
                Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
            },
            ct).ConfigureAwait(false);
        await WaitForObservationRelayRemovalAsync(scopeKey, targetActorId, ct).ConfigureAwait(false);
    }

    private async Task WaitForObservationRelayRemovalAsync(
        ProjectionRuntimeScopeKey scopeKey,
        string targetActorId,
        CancellationToken ct)
    {
        var authority = RequireBindingAuthority();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RelayReleaseTimeout);
        var startedAt = ProjectionActivationMetrics.StartTimestamp();

        try
        {
            while (await authority
                       .GetAsync(scopeKey.RootActorId, targetActorId, timeout.Token)
                       .ConfigureAwait(false) != null)
            {
                await Task.Delay(RelayReleaseCheckInterval, timeout.Token).ConfigureAwait(false);
            }

            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ReleaseReadinessStage,
                startedAt,
                scopeKey.Mode,
                "success");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ReleaseReadinessStage,
                startedAt,
                scopeKey.Mode,
                "timeout");
            throw new TimeoutException(
                $"Timed out waiting for projection observation relay removal. root_actor_id={scopeKey.RootActorId} projection_kind={scopeKey.ProjectionKind} session_id={scopeKey.SessionId}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ReleaseReadinessStage,
                startedAt,
                scopeKey.Mode,
                "cancelled");
            throw;
        }
        catch
        {
            ProjectionActivationMetrics.RecordStage(
                ProjectionActivationMetrics.ReleaseReadinessStage,
                startedAt,
                scopeKey.Mode,
                "failure");
            throw;
        }
    }

    private IStreamForwardingBindingAuthority RequireBindingAuthority() =>
        _bindingAuthority ?? throw new InvalidOperationException(
            "IStreamForwardingBindingAuthority is required to prove projection relay release.");
}
