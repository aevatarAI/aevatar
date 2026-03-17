using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionMaterializationScopeReleaseService<TLease, TScopeAgent>
    : IProjectionMaterializationReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TScopeAgent : IAgent
{
    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<TLease, ProjectionRuntimeScopeKey> _scopeKeyAccessor;

    public ProjectionMaterializationScopeReleaseService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<TLease, ProjectionRuntimeScopeKey> scopeKeyAccessor,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(runtime, dispatchPort, agentTypeVerifier);
        _scopeKeyAccessor = scopeKeyAccessor ?? throw new ArgumentNullException(nameof(scopeKeyAccessor));
    }

    public async Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        var scopeKey = _scopeKeyAccessor(lease);
        if (!await _scopeRuntime.ExistsAsync(scopeKey, ct).ConfigureAwait(false))
            return;

        await _scopeRuntime.DispatchAsync(
            scopeKey,
            new ReleaseProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
            },
            ct).ConfigureAwait(false);
    }
}
