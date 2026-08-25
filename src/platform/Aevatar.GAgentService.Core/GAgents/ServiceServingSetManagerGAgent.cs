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
    internal const int MaximumResolvedOperationHistory = 64;

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
            Targets = { resolvedTargets.Select(target => CloneTarget(target)) },
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await DispatchInvocationServingObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleRemoveDeploymentAsync(RemoveDeploymentFromServiceServingTargetsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        var deploymentId = command.DeploymentId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(deploymentId))
            throw new InvalidOperationException("deployment_id is required.");

        var target = State.Targets.FirstOrDefault(target =>
            string.Equals(target.DeploymentId, deploymentId, StringComparison.Ordinal));
        if (target != null)
        {
            if (!MatchesRemovalFence(command, target))
            {
                await DispatchRemovedAckAsync(
                    command,
                    ServiceServingTargetRemovalDisposition.Superseded,
                    target,
                    CancellationToken.None);
                return;
            }

            var remainingTargets = State.Targets
                .Where(target => !string.Equals(target.DeploymentId, deploymentId, StringComparison.Ordinal))
                .Select(target => CloneTarget(target))
                .ToList();
            await PersistDomainEventAsync(new ServiceServingSetUpdatedEvent
            {
                Identity = command.Identity?.Clone(),
                Generation = State.Generation + 1,
                Targets = { remainingTargets },
                RolloutId = State.ActiveRolloutId ?? string.Empty,
                Reason = command.Reason ?? string.Empty,
                UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
            await DispatchInvocationServingObservationAsync(CancellationToken.None);
            await DispatchRemovedAckAsync(
                command,
                ServiceServingTargetRemovalDisposition.Removed,
                target,
                CancellationToken.None);
            return;
        }

        await DispatchRemovedAckAsync(
            command,
            ServiceServingTargetRemovalDisposition.AlreadyAbsent,
            actualTarget: null,
            CancellationToken.None);
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
        if (command.OperationSequence <= 0)
            throw new InvalidOperationException("operation_sequence must be positive for activation serving updates.");

        var target = command.Targets[0];
        if (State.ResolvedOperations.TryGetValue(operationId, out var appliedOperation))
        {
            EnsureResolvedOperationMatches(
                appliedOperation,
                activationAttemptId,
                replyActorId,
                command.OperationSequence,
                target);
            await DispatchInvocationServingObservationAsync(CancellationToken.None);
            await DispatchAppliedAckAsync(appliedOperation, CancellationToken.None);
            return;
        }

        if (command.OperationSequence <= State.LastResolvedOperationSequence)
        {
            await DispatchSupersededAppliedAckAsync(command, CancellationToken.None);
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
            Targets = { command.Targets.Select(target => CloneTarget(target, operationId, activationAttemptId)) },
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            ResolvedOperationId = operationId,
            ResolvedActivationAttemptId = activationAttemptId,
            ResolvedReplyActorId = replyActorId,
            ResolvedDeploymentId = target?.DeploymentId ?? string.Empty,
            ResolvedRevisionId = target?.RevisionId ?? string.Empty,
            ResolvedOperationSequence = command.OperationSequence,
        });
    }

    private static bool MatchesRemovalFence(
        RemoveDeploymentFromServiceServingTargetsCommand command,
        ServiceServingTargetSpec target)
    {
        if (!MatchesOptionalFence(command.RevisionId, target.RevisionId) ||
            !MatchesOptionalFence(command.PrimaryActorId, target.PrimaryActorId) ||
            !MatchesExactFence(command.ActivationAttemptId, target.ActivationAttemptId))
        {
            return false;
        }

        return MatchesExactFence(command.ServingTargetOperationId, target.ServingTargetOperationId);
    }

    private static bool MatchesOptionalFence(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.Ordinal);

    private static bool MatchesExactFence(string? expected, string? actual) =>
        string.Equals(
            expected?.Trim() ?? string.Empty,
            actual?.Trim() ?? string.Empty,
            StringComparison.Ordinal);

    private static void EnsureResolvedOperationMatches(
        ServiceServingResolvedOperationRecord appliedOperation,
        string activationAttemptId,
        string replyActorId,
        long operationSequence,
        ServiceServingTargetSpec target)
    {
        if (!string.Equals(appliedOperation.ActivationAttemptId, activationAttemptId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.ReplyActorId, replyActorId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.DeploymentId, target.DeploymentId, StringComparison.Ordinal) ||
            !string.Equals(appliedOperation.RevisionId, target.RevisionId, StringComparison.Ordinal) ||
            appliedOperation.OperationSequence != operationSequence ||
            appliedOperation.Target == null ||
            !MatchesResolvedTarget(appliedOperation.Target, target))
        {
            throw new InvalidOperationException("operation_id is already bound to different serving update facts.");
        }
    }

    private static bool MatchesResolvedTarget(ServiceServingTargetSpec appliedTarget, ServiceServingTargetSpec requestedTarget) =>
        string.Equals(appliedTarget.DeploymentId, requestedTarget.DeploymentId, StringComparison.Ordinal) &&
        string.Equals(appliedTarget.RevisionId, requestedTarget.RevisionId, StringComparison.Ordinal) &&
        string.Equals(appliedTarget.PrimaryActorId, requestedTarget.PrimaryActorId, StringComparison.Ordinal) &&
        appliedTarget.AllocationWeight == requestedTarget.AllocationWeight &&
        appliedTarget.ServingState == requestedTarget.ServingState &&
        appliedTarget.EnabledEndpointIds.SequenceEqual(requestedTarget.EnabledEndpointIds);

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
        next.Targets.Add(evt.Targets.Select(target => CloneTarget(target)));
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
                OperationSequence = evt.ResolvedOperationSequence,
            };
            next.LastResolvedOperationSequence = Math.Max(
                next.LastResolvedOperationSequence,
                evt.ResolvedOperationSequence);
            foreach (var staleOperationId in next.ResolvedOperations.Values
                         .OrderByDescending(static operation => operation.OperationSequence)
                         .ThenByDescending(static operation => operation.ServingGeneration)
                         .ThenBy(static operation => operation.OperationId, StringComparer.Ordinal)
                         .Skip(MaximumResolvedOperationHistory)
                         .Select(static operation => operation.OperationId)
                         .ToArray())
            {
                next.ResolvedOperations.Remove(staleOperationId);
            }
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

    private static ServiceServingTargetSpec CloneTarget(
        ServiceServingTargetSpec source,
        string servingTargetOperationId = "",
        string activationAttemptId = "") =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState,
            EnabledEndpointIds = { source.EnabledEndpointIds },
            ServingTargetOperationId = string.IsNullOrWhiteSpace(servingTargetOperationId)
                ? source.ServingTargetOperationId ?? string.Empty
                : servingTargetOperationId,
            ActivationAttemptId = string.IsNullOrWhiteSpace(activationAttemptId)
                ? source.ActivationAttemptId ?? string.Empty
                : activationAttemptId,
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
                    ServingTargets = { State.Targets.Select(target => CloneTarget(target)) },
                    SourceServingVersion = State.LastAppliedEventVersion,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Service invocation serving observation was not admitted.");
    }

    private async Task DispatchRemovedAckAsync(
        RemoveDeploymentFromServiceServingTargetsCommand command,
        ServiceServingTargetRemovalDisposition disposition,
        ServiceServingTargetSpec? actualTarget,
        CancellationToken ct)
    {
        var replyActorId = command.ReplyActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(replyActorId))
            return;

        var admission = await _dispatchPort.DispatchAsync(
            replyActorId,
            CreateEnvelope(
                replyActorId,
                $"service-serving-removed:{ServiceKeys.Build(command.Identity!)}:{command.DeploymentId}:{command.DeactivationOperationId}",
                new ServiceServingTargetsRemovedAck
                {
                    Identity = command.Identity?.Clone(),
                    DeploymentId = command.DeploymentId,
                    RevisionId = command.RevisionId,
                    PrimaryActorId = command.PrimaryActorId,
                    ServingTargetOperationId = command.ServingTargetOperationId,
                    ActivationAttemptId = command.ActivationAttemptId,
                    RemovedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    DeactivationOperationId = command.DeactivationOperationId,
                    Disposition = disposition,
                    ActualRevisionId = actualTarget?.RevisionId ?? string.Empty,
                    ActualPrimaryActorId = actualTarget?.PrimaryActorId ?? string.Empty,
                    ActualServingTargetOperationId = actualTarget?.ServingTargetOperationId ?? string.Empty,
                    ActualActivationAttemptId = actualTarget?.ActivationAttemptId ?? string.Empty,
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Service serving targets removed acknowledgment was not admitted.");
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
                    OperationSequence = appliedOperation.OperationSequence,
                    Disposition = ServiceServingTargetsApplyDisposition.Applied,
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Service serving targets applied acknowledgment was not admitted.");
    }

    private async Task DispatchSupersededAppliedAckAsync(
        ReplaceResolvedServiceServingTargetsCommand command,
        CancellationToken ct)
    {
        var admission = await _dispatchPort.DispatchAsync(
            command.ReplyActorId,
            CreateEnvelope(
                command.ReplyActorId,
                $"service-serving-superseded:{command.OperationId}",
                new ServiceServingTargetsAppliedAck
                {
                    Identity = command.Identity?.Clone(),
                    RevisionId = command.Targets[0].RevisionId,
                    DeploymentId = command.Targets[0].DeploymentId,
                    ActivationAttemptId = command.ActivationAttemptId,
                    OperationId = command.OperationId,
                    OperationSequence = command.OperationSequence,
                    ServingGeneration = State.Generation,
                    AppliedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    Disposition = ServiceServingTargetsApplyDisposition.Superseded,
                    SupersededByOperationSequence = State.LastResolvedOperationSequence,
                }),
            ct);
        if (!admission.Accepted)
            throw new InvalidOperationException("Superseded service serving acknowledgment was not admitted.");
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
