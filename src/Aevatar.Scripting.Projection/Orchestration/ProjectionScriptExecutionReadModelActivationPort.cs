using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ProjectionScriptExecutionReadModelActivationPort
    : IScriptExecutionReadModelActivationPort
{
    private readonly ScriptExecutionReadModelPort _projectionPort;

    public ProjectionScriptExecutionReadModelActivationPort(
        ScriptExecutionReadModelPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<bool> ActivateAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        return await _projectionPort.EnsureActorProjectionAsync(actorId, ct) != null;
    }
}
