using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionLifecycleService
    : ScriptEvolutionProjectionLifecyclePortServiceBase,
      IScriptEvolutionProjectionLifecyclePort
{
    private const string ProjectionName = "script-evolution-session";

    public ScriptEvolutionProjectionLifecycleService(
        ScriptEvolutionProjectionOptions options,
        IProjectionPortActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionPortReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IScriptEvolutionProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IScriptEvolutionProjectionLiveSinkForwarder liveSinkForwarder)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public bool ProjectionEnabled => ProjectionEnabledCore;

    public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            sessionActorId,
            ProjectionName,
            input: string.Empty,
            commandId: proposalId,
            ct);

    public Task AttachLiveSinkAsync(
        IScriptEvolutionProjectionLease lease,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachLiveSinkAsync(
        IScriptEvolutionProjectionLease lease,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseActorProjectionAsync(
        IScriptEvolutionProjectionLease lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);
}
