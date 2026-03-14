using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.ReadPorts;

public sealed class ProjectionScriptAuthorityProjectionPrimingPort : IScriptAuthorityProjectionPrimingPort
{
    private readonly ScriptAuthorityProjectionPortService _projectionPort;

    public ProjectionScriptAuthorityProjectionPrimingPort(
        ScriptAuthorityProjectionPortService projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task PrimeAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _ = await _projectionPort.EnsureActorProjectionAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Script authority projection is disabled for actor `{actorId}`.");
    }
}
