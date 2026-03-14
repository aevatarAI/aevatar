using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionPortService
    : EventSinkProjectionLifecyclePortServiceBase<IProjectionPortSessionLease, ScriptAuthorityRuntimeLease, EventEnvelope>
{
    private const string ProjectionName = "script-authority-read-model";

    public ScriptAuthorityProjectionPortService(
        IProjectionPortActivationService<ScriptAuthorityRuntimeLease> activationService,
        IProjectionPortReleaseService<ScriptAuthorityRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<ScriptAuthorityRuntimeLease, EventEnvelope> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<ScriptAuthorityRuntimeLease, EventEnvelope> liveSinkForwarder)
        : base(
            static () => true,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public Task<IProjectionPortSessionLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            actorId,
            ProjectionName,
            input: string.Empty,
            commandId: actorId,
            ct);
}
