using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionPort
    : MaterializationProjectionPortBase<ScriptAuthorityRuntimeLease>
{
    private const string ProjectionName = "script-authority-read-model";

    public ScriptAuthorityProjectionPort(
        IProjectionMaterializationActivationService<ScriptAuthorityRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<ScriptAuthorityRuntimeLease> releaseService)
        : base(
            static () => true,
            activationService,
            releaseService)
    {
    }

    public Task<ScriptAuthorityRuntimeLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionName,
            },
            ct);
}
