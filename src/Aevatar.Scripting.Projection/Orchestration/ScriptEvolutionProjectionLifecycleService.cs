using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionLifecycleService
    : EventSinkProjectionLifecyclePortServiceBase<IScriptEvolutionProjectionLease, ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionLifecyclePort
{
    private const string ProjectionName = "script-evolution-session";

    public ScriptEvolutionProjectionLifecycleService(
        ScriptEvolutionProjectionOptions options,
        IProjectionPortActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionPortReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<ScriptEvolutionRuntimeLease, IEventSink<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionCompletedEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<ScriptEvolutionRuntimeLease, IEventSink<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionCompletedEvent> liveSinkForwarder)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder,
            ResolveRuntimeLeaseOrThrow)
    {
    }

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

    private static ScriptEvolutionRuntimeLease ResolveRuntimeLeaseOrThrow(IScriptEvolutionProjectionLease lease) =>
        lease as ScriptEvolutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported scripting evolution projection lease implementation.");
}
