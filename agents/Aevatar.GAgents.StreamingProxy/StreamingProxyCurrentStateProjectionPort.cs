using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyCurrentStateProjectionPort
    : MaterializationProjectionPortBase<StreamingProxyCurrentStateRuntimeLease>
{
    public StreamingProxyCurrentStateProjectionPort(
        IProjectionScopeActivationService<StreamingProxyCurrentStateRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<StreamingProxyCurrentStateRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = StreamingProxyProjectionKinds.CurrentState,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
