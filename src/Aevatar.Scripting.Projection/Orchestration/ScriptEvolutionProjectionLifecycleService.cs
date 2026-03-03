using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionLifecycleService
    : ProjectionLifecyclePortServiceBase<
        IScriptEvolutionProjectionLease,
        ScriptEvolutionRuntimeLease,
        IScriptEvolutionEventSink,
        ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionLifecyclePort
{
    private const string ProjectionName = "script-evolution-session";

    public ScriptEvolutionProjectionLifecycleService(
        ScriptEvolutionProjectionOptions options,
        IProjectionPortActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionPortReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<
            ScriptEvolutionRuntimeLease,
            IScriptEvolutionEventSink,
            ScriptEvolutionSessionCompletedEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<
            ScriptEvolutionRuntimeLease,
            IScriptEvolutionEventSink,
            ScriptEvolutionSessionCompletedEvent> liveSinkForwarder)
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
        IScriptEvolutionEventSink sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachLiveSinkAsync(
        IScriptEvolutionProjectionLease lease,
        IScriptEvolutionEventSink sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseActorProjectionAsync(
        IScriptEvolutionProjectionLease lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);

    protected override ScriptEvolutionRuntimeLease ResolveRuntimeLease(IScriptEvolutionProjectionLease lease) =>
        lease as ScriptEvolutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported scripting evolution projection lease implementation.");
}
