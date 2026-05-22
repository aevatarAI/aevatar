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

    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
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
