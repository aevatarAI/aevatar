using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.AGUI.Contracts;

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
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    Task<EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>?> AttachExistingRunProjectionAsync(
        string actorId,
        string runId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default);
}
