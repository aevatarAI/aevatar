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
    private const string ProjectionName = "script-execution-read-model";

    public ScriptExecutionProjectionPort(
        ScriptExecutionProjectionOptions options,
        IProjectionSessionActivationService<ScriptExecutionRuntimeLease> activationService,
        IProjectionSessionReleaseService<ScriptExecutionRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<ScriptExecutionRuntimeLease, EventEnvelope> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<ScriptExecutionRuntimeLease, EventEnvelope> liveSinkForwarder)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionSessionStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionName,
                SessionId = actorId,
            },
            ct);
}
