namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeAttachExistingLeaseLookup<TLease, TContext>
    : IProjectionScopeAttachExistingLeaseLookup<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TContext : class, IProjectionMaterializationContext
{
    private readonly IActorRuntime _runtime;
    private readonly Func<ProjectionScopeStartRequest, TContext> _contextFactory;
    private readonly Func<ProjectionRuntimeScopeKey, TContext, TLease> _leaseFactory;

    public ProjectionScopeAttachExistingLeaseLookup(
        IActorRuntime runtime,
        Func<ProjectionScopeStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
    }

    public async Task<TLease?> TryGetAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
        // Refactor (iter49/cluster-049-gagentservice-runtime-attach-existing-side-read):
        //   Old pattern: Capability projection ports duplicated runtime existence checks via IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()).
        //   New principle: Projection Core exposes typed attach-existing lease/session lookup contract; capability ports delegate to contract instead of runtime actor-id side reads.
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var context = _contextFactory(request);
        var scopeKey = new ProjectionRuntimeScopeKey(
            context.RootActorId,
            context.ProjectionKind,
            request.Mode,
            request.SessionId);

        return await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false)
            ? _leaseFactory(scopeKey, context)
            : null;
    }
}
