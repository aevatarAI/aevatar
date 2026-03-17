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
    public ScriptEvolutionProjectionPort(
        ScriptEvolutionProjectionOptions options,
        IProjectionSessionActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionSessionReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sessionEventHub)
    {
    }

    public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionSessionStartRequest
            {
                RootActorId = sessionActorId,
                ProjectionKind = ScriptProjectionKinds.EvolutionSession,
                SessionId = proposalId,
            },
            ct);
}
