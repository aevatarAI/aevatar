using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Studio.Projection.Orchestration;

public sealed class StudioCurrentStateProjectionPort
    : MaterializationProjectionPortBase<StudioMaterializationRuntimeLease>
{
    public const string ProjectionKind = "studio-current-state";

    public StudioCurrentStateProjectionPort(
        IProjectionScopeActivationService<StudioMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<StudioMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
