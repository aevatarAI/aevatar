using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IScriptEvolutionProjectionLease, ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionPort
{
    private readonly IActorRuntime _runtime;

    public ScriptEvolutionProjectionPort(
        ScriptEvolutionProjectionOptions options,
        IProjectionScopeActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionScopeReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub,
        IActorRuntime runtime)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = sessionActorId,
                ProjectionKind = ScriptProjectionKinds.EvolutionSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = proposalId,
            },
            ct);

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
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(sessionActorId) ||
            string.IsNullOrWhiteSpace(proposalId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            sessionActorId,
            ScriptProjectionKinds.EvolutionSession,
            ProjectionRuntimeMode.SessionObservation,
            proposalId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new ScriptEvolutionRuntimeLease(new ScriptEvolutionSessionProjectionContext
        {
            RootActorId = sessionActorId,
            ProjectionKind = ScriptProjectionKinds.EvolutionSession,
            SessionId = proposalId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IScriptEvolutionProjectionLease>(lease, liveSinkLease);
    }
}
