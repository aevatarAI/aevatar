using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionSessionScopeActivationService<TLease, TContext, TScopeAgent>
    : IProjectionSessionActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TContext : class, IProjectionSessionContext
    where TScopeAgent : IAgent
{
    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<ProjectionSessionStartRequest, TContext> _contextFactory;
    private readonly Func<ProjectionRuntimeScopeKey, TContext, TLease> _leaseFactory;

    public ProjectionSessionScopeActivationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<ProjectionSessionStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(runtime, dispatchPort, agentTypeVerifier);
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
    }

    public async Task<TLease> EnsureAsync(
        ProjectionSessionStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var context = _contextFactory(request);
        var scopeKey = new ProjectionRuntimeScopeKey(
            context.RootActorId,
            context.ProjectionKind,
            ProjectionRuntimeMode.SessionObservation,
            context.SessionId);

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

        return _leaseFactory(scopeKey, context);
    }
}
