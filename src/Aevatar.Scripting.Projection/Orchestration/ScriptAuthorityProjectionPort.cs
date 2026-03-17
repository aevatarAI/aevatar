using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionPort
    : MaterializationProjectionPortBase<ScriptAuthorityRuntimeLease>,
      IScriptAuthorityReadModelActivationPort
{
    public ScriptAuthorityProjectionPort(
        IProjectionScopeActivationService<ScriptAuthorityRuntimeLease> activationService,
        IProjectionScopeReleaseService<ScriptAuthorityRuntimeLease> releaseService)
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
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.AuthorityMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);

    public async Task ActivateAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _ = await EnsureActorProjectionAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Script authority readmodel activation is disabled for actor `{actorId}`.");
    }
}
