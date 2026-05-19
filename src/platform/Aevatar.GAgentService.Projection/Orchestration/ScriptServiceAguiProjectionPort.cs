using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Lifecycle adapter for script service AGUI Projection Pipeline sessions.
/// It activates and releases actorized projection sessions and attaches the
/// typed AGUI sink used by host-facing script-run streams.
/// </summary>
public sealed class ScriptServiceAguiProjectionPort
    : EventSinkProjectionLifecyclePortBase<IScriptServiceAguiProjectionLease, ScriptServiceAguiRuntimeLease, AGUIEvent>,
      IScriptServiceAguiProjectionPort
{
    public ScriptServiceAguiProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ScriptServiceAguiRuntimeLease> activationService,
        IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
    }

    public Task<IScriptServiceAguiProjectionLease?> EnsureRunProjectionAsync(
        string actorId,
        string runId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ServiceProjectionKinds.ScriptServiceAguiSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = runId,
            },
            ct);
}
