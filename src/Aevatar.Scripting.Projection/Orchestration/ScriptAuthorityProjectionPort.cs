using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionPort
    : MaterializationProjectionPortBase<ScriptAuthorityRuntimeLease>,
      IScriptAuthorityReadModelActivationPort
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

    public async Task ActivateAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _ = await EnsureActorProjectionAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Script authority readmodel activation is disabled for actor `{actorId}`.");
    }
}
