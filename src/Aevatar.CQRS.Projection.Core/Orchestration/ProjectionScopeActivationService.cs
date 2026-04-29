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
    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<ProjectionScopeStartRequest, TContext> _contextFactory;
    private readonly Func<ProjectionRuntimeScopeKey, TContext, TLease> _leaseFactory;

    public ProjectionScopeActivationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<ProjectionScopeStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory,
        IAgentTypeVerifier? agentTypeVerifier = null,
        IStreamPubSubMaintenance? streamPubSubMaintenance = null,
        ILoggerFactory? loggerFactory = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(
            runtime,
            dispatchPort,
            agentTypeVerifier,
            streamPubSubMaintenance,
            loggerFactory?.CreateLogger<ProjectionScopeActorRuntime<TScopeAgent>>());
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
    }

    public async Task<TLease> EnsureAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
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

        return _leaseFactory(scopeKey, context);
    }
}
