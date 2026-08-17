using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.serving-set-manager")]
public sealed class ServiceServingSetManagerGAgent : GAgentBase<ServiceServingSetState>
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceServingTargetResolver _targetResolver;

    public ServiceServingSetManagerGAgent(
        IActorDispatchPort dispatchPort,
        IServiceServingTargetResolver targetResolver)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleReplaceAsync(ReplaceServiceServingTargetsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        var resolvedTargets = await _targetResolver.ResolveTargetsAsync(command.Identity!, command.Targets, CancellationToken.None);
        ValidateTargets(resolvedTargets);

        await PersistDomainEventAsync(new ServiceServingSetUpdatedEvent
        {
            Identity = command.Identity?.Clone(),
            Generation = State.Generation + 1,
            Targets = { resolvedTargets.Select(CloneTarget) },
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationServingObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReplaceResolvedAsync(ReplaceResolvedServiceServingTargetsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        ValidateTargets(command.Targets);

        var operationId = command.OperationId?.Trim() ?? string.Empty;
        var activationAttemptId = command.ActivationAttemptId?.Trim() ?? string.Empty;
        var replyActorId = command.ReplyActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(operationId))
        {
            if (!string.IsNullOrEmpty(activationAttemptId) || !string.IsNullOrEmpty(replyActorId))
                throw new InvalidOperationException("operation_id is required for activation serving updates.");

            await PersistResolvedTargetsAsync(command, operationId, activationAttemptId, replyActorId);
            await DispatchInvocationServingObservationAsync(CancellationToken.None);
            return;
        }

        if (string.IsNullOrEmpty(replyActorId))
            throw new InvalidOperationException("reply_actor_id is required for activation serving updates.");
        if (command.Targets.Count != 1)
            throw new InvalidOperationException("Activation serving updates require exactly one target.");

        var target = command.Targets[0];
        if (State.ResolvedOperations.TryGetValue(operationId, out var appliedOperation))
        {
            EnsureResolvedOperationMatches(appliedOperation, activationAttemptId, replyActorId, target);
            await DispatchInvocationServingObservationAsync(CancellationToken.None);
            await DispatchAppliedAckAsync(appliedOperation, CancellationToken.None);
            return;
        }

        await PersistResolvedTargetsAsync(command, operationId, activationAttemptId, replyActorId);
        await DispatchInvocationServingObservationAsync(CancellationToken.None);
        await DispatchAppliedAckAsync(State.ResolvedOperations[operationId], CancellationToken.None);
    }

    private Task PersistResolvedTargetsAsync(
        ReplaceResolvedServiceServingTargetsCommand command,
        string operationId,
        string activationAttemptId,
        string replyActorId)
    {
        var target = command.Targets.Count == 1 ? command.Targets[0] : null;
        return PersistDomainEventAsync(new ServiceServingSetUpdatedEvent
        {
            Identity = command.Identity?.Clone(),
            Generation = State.Generation + 1,
            Targets = { command.Targets.Select(CloneTarget) },
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            ResolvedOperationId = operationId,
            ResolvedActivationAttemptId = activationAttemptId,
            ResolvedReplyActorId = replyActorId,
            ResolvedDeploymentId = target?.DeploymentId ?? string.Empty,
            ResolvedRevisionId = target?.RevisionId ?? string.Empty,
        });
    }

    private static void EnsureResolvedOperationMatches(
        ServiceServingResolvedOperationRecord appliedOperation,
        string activationAttemptId,
        string replyActorId,
        ServiceServingTargetSpec target)
    {
        if (!string.Equals(appliedOperation.ActivationAttemptId, activationAttemptId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.ReplyActorId, replyActorId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.DeploymentId, target.DeploymentId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.RevisionId, target.RevisionId, StringComparison.Ordinal) ||
            appliedOperation.Target == null ||
            !appliedOperation.Target.Equals(target))
        {
            throw new InvalidOperationException("operation_id is already bound to different serving update facts.");
        }
    }

    protected override ServiceServingSetState TransitionState(ServiceServingSetState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceServingSetUpdatedEvent>(ApplyUpdated)
            .OrCurrent();

    private static ServiceServingSetState ApplyUpdated(ServiceServingSetState state, ServiceServingSetUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.Generation = evt.Generation;
        next.ActiveRolloutId = evt.RolloutId ?? string.Empty;
        next.Targets.Clear();
        next.Targets.Add(evt.Targets.Select(CloneTarget));
        next.UpdatedAt = evt.UpdatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(evt.ResolvedOperationId))
        {
            next.LastResolvedOperationId = evt.ResolvedOperationId;
            next.LastResolvedActivationAttemptId = evt.ResolvedActivationAttemptId ?? string.Empty;
            next.LastResolvedReplyActorId = evt.ResolvedReplyActorId ?? string.Empty;
            next.LastResolvedDeploymentId = evt.ResolvedDeploymentId ?? string.Empty;
            next.LastResolvedRevisionId = evt.ResolvedRevisionId ?? string.Empty;
            next.LastResolvedAppliedAt = evt.UpdatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
            next.ResolvedOperations[evt.ResolvedOperationId] = new ServiceServingResolvedOperationRecord
            {
                OperationId = evt.ResolvedOperationId,
                ActivationAttemptId = evt.ResolvedActivationAttemptId ?? string.Empty,
                ReplyActorId = evt.ResolvedReplyActorId ?? string.Empty,
                DeploymentId = evt.ResolvedDeploymentId ?? string.Empty,
                RevisionId = evt.ResolvedRevisionId ?? string.Empty,
                ServingGeneration = evt.Generation,
                AppliedAt = next.LastResolvedAppliedAt.Clone(),
                Target = evt.Targets.Count == 1
                    ? CloneTarget(evt.Targets[0])
                    : new ServiceServingTargetSpec(),
            };
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.Generation);
        return next;
    }

    private void EnsureIdentity(ServiceIdentity? identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service serving set '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service serving actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static void ValidateTargets(IEnumerable<ServiceServingTargetSpec> targets)
    {
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.DeploymentId))
                throw new InvalidOperationException("deployment_id is required.");
            if (string.IsNullOrWhiteSpace(target.RevisionId))
                throw new InvalidOperationException("revision_id is required.");
            if (string.IsNullOrWhiteSpace(target.PrimaryActorId))
                throw new InvalidOperationException("primary_actor_id is required.");
            if (target.AllocationWeight < 0)
                throw new InvalidOperationException("allocation_weight must be non-negative.");
        }
    }

    private static string BuildEventId(ServiceIdentity? identity, long generation)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:serving:{generation}";
    }

    private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState,
            EnabledEndpointIds = { source.EnabledEndpointIds },
        };

    private async Task DispatchInvocationServingObservationAsync(CancellationToken ct)
    {
        var identity = State.Identity;
        if (identity == null || string.IsNullOrWhiteSpace(identity.ServiceId))
            return;

        var actorId = ServiceActorIds.InvocationCatalog(identity);
        var admission = await _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                $"service-serving-observation:{ServiceKeys.Build(identity)}:{State.LastAppliedEventVersion}",
                new ObserveServiceInvocationServingCommand
                {
                    Identity = identity.Clone(),
                    ServingTargets = { State.Targets.Select(CloneTarget) },
                    SourceServingVersion = State.LastAppliedEventVersion,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Service invocation serving observation was not admitted.");
    }

    private async Task DispatchAppliedAckAsync(
        ServiceServingResolvedOperationRecord appliedOperation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appliedOperation.OperationId) ||
            string.IsNullOrWhiteSpace(appliedOperation.ReplyActorId))
        {
            throw new InvalidOperationException("Committed activation serving update is missing acknowledgment facts.");
        }

        var admission = await _dispatchPort.DispatchAsync(
            appliedOperation.ReplyActorId,
            CreateEnvelope(
                appliedOperation.ReplyActorId,
                $"service-serving-applied:{appliedOperation.OperationId}",
                new ServiceServingTargetsAppliedAck
                {
                    Identity = State.Identity?.Clone(),
                    RevisionId = appliedOperation.RevisionId,
                    DeploymentId = appliedOperation.DeploymentId,
                    ActivationAttemptId = appliedOperation.ActivationAttemptId,
                    OperationId = appliedOperation.OperationId,
                    ServingGeneration = appliedOperation.ServingGeneration,
                    AppliedAt = appliedOperation.AppliedAt?.Clone()
                                ?? Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Service serving targets applied acknowledgment was not admitted.");
    }

    private EventEnvelope CreateEnvelope(string actorId, string envelopeId, IMessage payload) =>
        new()
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(Id, actorId),
            Propagation = new EnvelopePropagation(),
        };

}
