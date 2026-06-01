using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IScriptEvolutionProjectionLease, ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<ScriptEvolutionRuntimeLease> _attachExistingLeaseLookup;

    public ScriptEvolutionProjectionPort(
        ScriptEvolutionProjectionOptions options,
        IProjectionScopeReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<ScriptEvolutionRuntimeLease> attachExistingLeaseLookup)
        : base(
            () => options?.Enabled ?? false,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    // Refactor (iter41/cluster-041-command-observation-projection-activation):
    //   Old pattern: command observation binders ensure/activate projection/readmodel sessions before dispatch.
    //   New principle: observation binders attach only to existing projection-owned sessions;
    //   activation happens in projection-owned startup/background/committed-state lifecycle.
    public async Task<EventSinkProjectionAttachment<IScriptEvolutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct = default)
    {
        // Refactor (iter101/cluster-104): Old command observation path could reach activation through the lifecycle base; new path is attach-existing session/readmodel only.
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(sessionActorId) ||
            string.IsNullOrWhiteSpace(proposalId))
        {
            return null;
        }

        // Refactor (iter51/issue-898-projection-attach-existing-side-read):
        //   Old pattern: Feature projection ports duplicated IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()) for attach-existing checks (post-#884 #884 fixed 3 ports but more remained).
        //   New principle: All attach-existing lease lookups go through typed IProjectionScopeAttachExistingLeaseLookup<TLease>; CI guard prevents recurrence.
        var lease = await _attachExistingLeaseLookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = sessionActorId,
            ProjectionKind = ScriptProjectionKinds.EvolutionSession,
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = proposalId,
        }, ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IScriptEvolutionProjectionLease>(lease, liveSinkLease);
    }
}
