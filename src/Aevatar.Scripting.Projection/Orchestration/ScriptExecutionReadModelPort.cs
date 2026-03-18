using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionReadModelPort
    : MaterializationProjectionPortBase<ScriptExecutionMaterializationRuntimeLease>,
      IScriptExecutionReadModelActivationPort
{
    public ScriptExecutionReadModelPort(
        ScriptExecutionProjectionOptions options,
        IProjectionScopeActivationService<ScriptExecutionMaterializationRuntimeLease> activationService,
        IProjectionScopeReleaseService<ScriptExecutionMaterializationRuntimeLease> releaseService)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService)
    {
    }

    public Task<ScriptExecutionMaterializationRuntimeLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.ExecutionMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);

    public async Task<bool> ActivateAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        return await EnsureActorProjectionAsync(actorId, ct) != null;
    }
}
