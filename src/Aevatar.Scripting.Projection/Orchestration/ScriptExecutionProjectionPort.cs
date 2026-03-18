using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IScriptExecutionProjectionLease, ScriptExecutionRuntimeLease, EventEnvelope>,
      IScriptExecutionProjectionPort
{
    public ScriptExecutionProjectionPort(
        ScriptExecutionProjectionOptions options,
        IProjectionSessionActivationService<ScriptExecutionRuntimeLease> activationService,
        IProjectionSessionReleaseService<ScriptExecutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<EventEnvelope> sessionEventHub)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sessionEventHub)
    {
    }

    public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionSessionStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.ExecutionSession,
                SessionId = actorId,
            },
            ct);
}
