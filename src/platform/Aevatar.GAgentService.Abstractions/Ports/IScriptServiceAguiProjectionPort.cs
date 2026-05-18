using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Host-facing lease returned for a typed AGUI projection session backing a script
/// service run. It identifies the actor and run session observed through the
/// Projection Pipeline lifecycle.
/// </summary>
public interface IScriptServiceAguiProjectionLease
{
    string ActorId { get; }

    string RunId { get; }
}

/// <summary>
/// Host-facing lifecycle port for typed AGUI projection sessions that back script
/// service runs. Activation and release happen here while pipeline input is
/// consumed downstream by the session projector.
/// </summary>
public interface IScriptServiceAguiProjectionPort
    : IEventSinkProjectionLifecyclePort<IScriptServiceAguiProjectionLease, AGUIEvent>
{
    Task<IScriptServiceAguiProjectionLease?> EnsureRunProjectionAsync(
        string actorId,
        string runId,
        CancellationToken ct = default);
}
