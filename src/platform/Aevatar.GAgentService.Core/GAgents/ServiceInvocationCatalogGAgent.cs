using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Services;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.invocation-catalog")]
public sealed class ServiceInvocationCatalogGAgent : GAgentBase<ServiceInvocationCatalogState>
{
    private readonly ServiceInvokeReadinessEvaluator _evaluator;

    public ServiceInvocationCatalogGAgent(ServiceInvokeReadinessEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        InitializeId();
    }

    [EventHandler]
    public Task HandleCatalogObservationAsync(ObserveServiceInvocationCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        if (command.SourceCatalogVersion < State.SourceCatalogVersion)
            return Task.CompletedTask;

        var next = State.Clone();
        next.Identity = command.Identity.Clone();
        next.ServiceEndpoints.Clear();
        next.ServiceEndpoints.Add(command.ServiceEndpoints.Select(CloneEndpoint));
        next.SourceCatalogVersion = command.SourceCatalogVersion;
        next.ObservedAt = ResolveObservedAt(command.ObservedAt);
        return PersistObservationAsync(next);
    }

    [EventHandler]
    public Task HandleServingObservationAsync(ObserveServiceInvocationServingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        if (command.SourceServingVersion < State.SourceServingVersion)
            return Task.CompletedTask;

        var next = State.Clone();
        next.Identity = command.Identity.Clone();
        next.ServingTargets.Clear();
        next.ServingTargets.Add(command.ServingTargets.Select(CloneTarget));
        next.SourceServingVersion = command.SourceServingVersion;
        next.ObservedAt = ResolveObservedAt(command.ObservedAt);
        return PersistObservationAsync(next);
    }

    [EventHandler]
    public Task HandleRevisionObservationAsync(ObserveServiceInvocationRevisionsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        if (command.SourceRevisionVersion < State.SourceRevisionVersion)
            return Task.CompletedTask;

        var next = State.Clone();
        next.Identity = command.Identity.Clone();
        next.Revisions.Clear();
        next.RevisionReadiness.Clear();
        if (command.RevisionReadiness.Count > 0)
            next.RevisionReadiness.Add(CloneRevisionReadiness(command.RevisionReadiness));
        else
            next.RevisionReadiness.Add(ServiceInvocationCatalogCompaction.ProjectRevisions(command.Revisions));
        next.SourceRevisionVersion = command.SourceRevisionVersion;
        next.ObservedAt = ResolveObservedAt(command.ObservedAt);
        return PersistObservationAsync(next);
    }

    protected override ServiceInvocationCatalogState TransitionState(ServiceInvocationCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceInvocationCatalogObservedEvent>(ApplyObserved)
            .OrCurrent();

    private async Task PersistObservationAsync(ServiceInvocationCatalogState next)
    {
        var entries = _evaluator.Evaluate(
            next.ServiceEndpoints,
            next.ServingTargets,
            next.RevisionReadiness);

        await PersistDomainEventAsync(new ServiceInvocationCatalogObservedEvent
        {
            Identity = next.Identity?.Clone(),
            ServiceEndpoints = { next.ServiceEndpoints.Select(CloneEndpoint) },
            ServingTargets = { next.ServingTargets.Select(CloneTarget) },
            RevisionReadiness = { CloneRevisionReadiness(next.RevisionReadiness) },
            Entries = { entries.Select(CloneEntry) },
            SourceCatalogVersion = next.SourceCatalogVersion,
            SourceServingVersion = next.SourceServingVersion,
            SourceRevisionVersion = next.SourceRevisionVersion,
            ObservedAt = next.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    private static ServiceInvocationCatalogState ApplyObserved(
        ServiceInvocationCatalogState state,
        ServiceInvocationCatalogObservedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.ServiceEndpoints.Clear();
        next.ServiceEndpoints.Add(evt.ServiceEndpoints.Select(CloneEndpoint));
        next.ServingTargets.Clear();
        next.ServingTargets.Add(evt.ServingTargets.Select(CloneTarget));
        next.Revisions.Clear();
        next.RevisionReadiness.Clear();
        if (evt.RevisionReadiness.Count > 0)
            next.RevisionReadiness.Add(CloneRevisionReadiness(evt.RevisionReadiness));
        else
            next.RevisionReadiness.Add(ServiceInvocationCatalogCompaction.ProjectRevisions(evt.Revisions));
        next.Entries.Clear();
        next.Entries.Add(evt.Entries.Select(CloneEntry));
        next.SourceCatalogVersion = evt.SourceCatalogVersion;
        next.SourceServingVersion = evt.SourceServingVersion;
        next.SourceRevisionVersion = evt.SourceRevisionVersion;
        next.ObservedAt = evt.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, next.LastAppliedEventVersion);
        return next;
    }

    private void EnsureIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service invocation catalog '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service invocation catalog actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static Timestamp ResolveObservedAt(Timestamp? observedAt) =>
        observedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);

    private static string BuildEventId(ServiceIdentity? identity, long version)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:invocation-catalog:{version}";
    }

    protected override Task OnStateChangedAsync(ServiceInvocationCatalogState state, CancellationToken ct)
    {
        ServiceInvocationCatalogCompaction.Compact(state);
        return Task.CompletedTask;
    }

    private static IDictionary<string, ServiceInvocationRevisionReadinessState> CloneRevisionReadiness(
        MapField<string, ServiceInvocationRevisionReadinessState> revisions) =>
        revisions.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);

    private static ServiceEndpointDescriptor CloneEndpoint(ServiceEndpointDescriptor source) =>
        source.Clone();

    private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
        source.Clone();

    private static ServiceInvocationCatalogEntryState CloneEntry(ServiceInvocationCatalogEntryState source) =>
        source.Clone();
}
