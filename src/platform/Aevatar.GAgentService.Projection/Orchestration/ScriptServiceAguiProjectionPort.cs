using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
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
    private readonly IActorRuntime _runtime;

    public ScriptServiceAguiProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ScriptServiceAguiRuntimeLease> activationService,
        IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IActorRuntime runtime)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>?> AttachExistingRunProjectionAsync(
        string actorId,
        string runId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            actorId,
            ServiceProjectionKinds.ScriptServiceAguiSession,
            ProjectionRuntimeMode.SessionObservation,
            runId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new ScriptServiceAguiRuntimeLease(new ScriptServiceAguiProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = ServiceProjectionKinds.ScriptServiceAguiSession,
            SessionId = runId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>(lease, liveSinkLease);
    }
}
