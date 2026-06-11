using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.AGUI.Contracts;

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
    private readonly IProjectionScopeAttachExistingLeaseLookup<ScriptServiceAguiRuntimeLease> _attachExistingLeaseLookup;

    public ScriptServiceAguiProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<ScriptServiceAguiRuntimeLease> attachExistingLeaseLookup)
        : base(
            () => options.Enabled,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
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
        // Refactor (iter101/cluster-104): Old event-sink lifecycle base could be used as an external ensure surface; this port now only attaches to existing projection-owned sessions.
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
        // Refactor (iter49/cluster-049-gagentservice-runtime-attach-existing-side-read):
        //   Old pattern: Capability projection ports duplicated runtime existence checks via IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()).
        //   New principle: Projection Core exposes typed attach-existing lease/session lookup contract; capability ports delegate to contract instead of runtime actor-id side reads.
        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                Mode = scopeKey.Mode,
                SessionId = scopeKey.SessionId,
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>(lease, liveSinkLease);
    }
}
