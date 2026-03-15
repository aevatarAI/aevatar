using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ProjectionScriptAuthorityReadModelActivationPort : IScriptAuthorityReadModelActivationPort
{
    private readonly ScriptAuthorityProjectionPortService _projectionPort;

    public ProjectionScriptAuthorityReadModelActivationPort(
        ScriptAuthorityProjectionPortService projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task ActivateAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _ = await _projectionPort.EnsureActorProjectionAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Script authority readmodel activation is disabled for actor `{actorId}`.");
    }
}
