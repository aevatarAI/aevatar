using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionReadModelPort
    : MaterializationProjectionPortBase<ScriptEvolutionMaterializationRuntimeLease>,
      IScriptEvolutionReadModelActivationPort
{
    private const string ProjectionName = "script-evolution-read-model";

    public ScriptEvolutionReadModelPort(
        ScriptEvolutionProjectionOptions options,
        IProjectionMaterializationActivationService<ScriptEvolutionMaterializationRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<ScriptEvolutionMaterializationRuntimeLease> releaseService)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService)
    {
    }

    public Task<ScriptEvolutionMaterializationRuntimeLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionName,
            },
            ct);

    public async Task<bool> ActivateAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        return await EnsureActorProjectionAsync(actorId, ct) != null;
    }
}
