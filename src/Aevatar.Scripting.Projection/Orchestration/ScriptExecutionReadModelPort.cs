using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionReadModelPort
    : MaterializationProjectionPortBase<ScriptExecutionMaterializationRuntimeLease>
{
    private const string ProjectionName = "script-execution-read-model";

    public ScriptExecutionReadModelPort(
        ScriptExecutionProjectionOptions options,
        IProjectionMaterializationActivationService<ScriptExecutionMaterializationRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<ScriptExecutionMaterializationRuntimeLease> releaseService)
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
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionName,
            },
            ct);
}
