using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;

namespace Aevatar.Scripting.Projection.Orchestration;

public interface IScriptEvolutionProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<ScriptEvolutionRuntimeLease, IEventSink<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionCompletedEvent>
{
}

public interface IScriptEvolutionProjectionLiveSinkForwarder
    : IProjectionPortLiveSinkForwarder<ScriptEvolutionRuntimeLease, IEventSink<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionCompletedEvent>
{
}

public interface IScriptEvolutionProjectionSinkFailurePolicy
    : IProjectionPortSinkFailurePolicy<ScriptEvolutionRuntimeLease, IEventSink<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionCompletedEvent>
{
}

public abstract class ScriptEvolutionProjectionLifecyclePortServiceBase
    : EventSinkProjectionLifecyclePortServiceBase<IScriptEvolutionProjectionLease, ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>
{
    protected ScriptEvolutionProjectionLifecyclePortServiceBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<ScriptEvolutionRuntimeLease> activationService,
        IProjectionPortReleaseService<ScriptEvolutionRuntimeLease> releaseService,
        IScriptEvolutionProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IScriptEvolutionProjectionLiveSinkForwarder liveSinkForwarder)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder,
            ResolveRuntimeLeaseOrThrow)
    {
    }

    private static ScriptEvolutionRuntimeLease ResolveRuntimeLeaseOrThrow(IScriptEvolutionProjectionLease lease) =>
        lease as ScriptEvolutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported scripting evolution projection lease implementation.");
}
