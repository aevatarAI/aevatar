using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.deployment-manager")]
public sealed class ServiceDeploymentManagerGAgent : GAgentBase<ServiceDeploymentState>
{
    private const string ActivationRetryCallbackPrefix = "service-deployment-activation-retry";
    private const string DeactivationRetryCallbackPrefix = "service-deployment-deactivation-retry";
    private static readonly TimeSpan ActivationRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationRetryBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ActivationRecoveryMinimumDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan DeactivationRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeactivationRetryBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeactivationRecoveryMinimumDelay = TimeSpan.FromMilliseconds(1);

    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly IActivationCapabilityViewReader _capabilityViewReader;
    private readonly IActivationAdmissionEvaluator _admissionEvaluator;
    private readonly IServiceRuntimeActivator _runtimeActivator;
    private readonly TimeProvider _timeProvider;

    public ServiceDeploymentManagerGAgent(
        IActorDispatchPort dispatchPort,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IActivationCapabilityViewReader capabilityViewReader,
        IActivationAdmissionEvaluator admissionEvaluator,
        IServiceRuntimeActivator runtimeActivator,
        TimeProvider? timeProvider = null)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _capabilityViewReader = capabilityViewReader ?? throw new ArgumentNullException(nameof(capabilityViewReader));
        _admissionEvaluator = admissionEvaluator ?? throw new ArgumentNullException(nameof(admissionEvaluator));
        _runtimeActivator = runtimeActivator ?? throw new ArgumentNullException(nameof(runtimeActivator));
        _timeProvider = timeProvider ?? TimeProvider.System;
        InitializeId();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Identity == null || string.IsNullOrWhiteSpace(State.Identity.ServiceId))
            return;

        foreach (var pending in State.PendingActivations.Values.ToArray())
        {
            await ScheduleActivationRetryAsync(
                State.Identity,
                pending,
                scheduleExpired: true,
                ct: ct);
        }

        foreach (var pending in State.PendingDeactivations.Values.ToArray())
        {
            await ScheduleDeactivationRetryAsync(
                State.Identity,
                pending,
                scheduleExpired: true,
                ct: ct);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleActivateAsync(ActivateServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: true);
        if (string.IsNullOrWhiteSpace(command.RevisionId))
            throw new InvalidOperationException("revision_id is required.");

        var activationAttemptId = NormalizeActivationAttemptId(command.ActivationAttemptId);
        var expectedArtifactHash = NormalizeExpectedArtifactHash(command.ExpectedArtifactHash);
        var isContinuation = command.ActivationDeadlineAt != null;
        if (ShouldIgnoreFencedActivation(
                command.RevisionId,
                activationAttemptId,
                expectedArtifactHash,
                isContinuation))
            return;

        var pending = await EnsurePendingActivationAsync(
            command.Identity,
            command.RevisionId,
            activationAttemptId,
            expectedArtifactHash);
        if (!await ResolveActivationDeactivationConflictAsync(
                command.Identity,
                pending))
        {
            return;
        }

        await ScheduleActivationRetryAsync(
            command.Identity,
            pending,
            scheduleExpired: false,
            ct: CancellationToken.None);

        if (IsActivationDeadlineReached(pending))
        {
            var failureCode = GetDeadlineFailureCode(pending);
            await FailActivationAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                failureCode,
                GetTerminalFailureReason(failureCode));
            return;
        }

        if (GetActivationPhase(pending) ==
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending)
        {
            await DispatchDefaultServingRevisionAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                pending);
            return;
        }

        ServiceRevisionCatalogSnapshot? revisionCatalog;
        try
        {
            revisionCatalog = await AwaitExternalWithinDeadlineAsync(
                pending,
                "revision-catalog-query",
                ct => _revisionCatalogQueryReader.GetAsync(command.Identity, ct));
            ArgumentNullException.ThrowIfNull(revisionCatalog);
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId))
        {
            return;
        }

        // The authoring chain dispatches prepare->publish->activate asynchronously, so activation waits
        // for the published revision projection and its artifact fence. Projection lag or a torn/stale
        // artifact snapshot re-arms the bounded self-continuation; preparation failure remains terminal.
        if (!revisionCatalog.TryGetPublishedPreparedArtifact(
                command.RevisionId,
                pending.ExpectedArtifactHash,
                out var artifact))
        {
            if (revisionCatalog.IsRevisionPreparationFailed(command.RevisionId))
            {
                await FailActivationAsync(
                    command.Identity,
                    command.RevisionId,
                    activationAttemptId,
                    ServiceDeploymentActivationFailureCode.RevisionPreparationFailed,
                    $"Prepared artifact for '{ServiceKeys.Build(command.Identity)}' revision '{command.RevisionId}' failed preparation.");
                return;
            }

            if (revisionCatalog.IsRevisionPublished(command.RevisionId))
            {
                await FailActivationAsync(
                    command.Identity,
                    command.RevisionId,
                    activationAttemptId,
                    ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
                    "Published prepared artifact failed integrity or artifact-fence validation.");
                return;
            }

            await DeferOrFailRetryableActivationAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
            return;
        }

        pending = State.PendingActivations[command.RevisionId];
        if (GetActivationPhase(pending) is
            ServiceDeploymentActivationPhase.ServingTargetDispatchPending or
            ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted)
        {
            pending = await EnsureServingTargetDispatchPendingAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                pending.DeploymentId,
                pending.PrimaryActorId);
            await DispatchServingTargetsAsync(
                command.Identity,
                command.RevisionId,
                activationAttemptId,
                pending,
                artifact);
            return;
        }

        await ActivatePreparedRevisionAsync(
            command.Identity,
            command.RevisionId,
            activationAttemptId,
            artifact);
    }

    private async Task ActivatePreparedRevisionAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        PreparedServiceRevisionArtifact artifact)
    {
        if (!TryGetMatchingPending(revisionId, activationAttemptId, out var pending))
            return;

        ActivationCapabilityView capabilityView;
        try
        {
            capabilityView = await AwaitExternalWithinDeadlineAsync(
                pending,
                "activation-capability-view-query",
                ct => _capabilityViewReader.GetAsync(identity, revisionId, ct));
            ArgumentNullException.ThrowIfNull(capabilityView);
        }
        catch (ActivationCapabilityViewNotReadyException exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                exception.Projection switch
                {
                    ActivationCapabilityViewProjection.ServiceCatalog =>
                        ServiceDeploymentActivationFailureCode.CapabilityViewNotReady,
                    _ => ServiceDeploymentActivationFailureCode.CapabilityViewNotReady,
                });
            return;
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(identity, revisionId, activationAttemptId))
            return;

        pending = State.PendingActivations[revisionId];

        ActivationAdmissionDecision admissionDecision;
        try
        {
            admissionDecision = await AwaitExternalWithinDeadlineAsync(
                pending,
                "activation-admission-evaluation",
                ct => _admissionEvaluator.EvaluateAsync(
                    new ActivationAdmissionRequest
                    {
                        CapabilityView = capabilityView,
                    },
                    ct));
            ArgumentNullException.ThrowIfNull(admissionDecision);
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(identity, revisionId, activationAttemptId))
            return;

        if (!admissionDecision.Allowed)
        {
            Logger.LogWarning(
                "Service activation admission rejected. serviceKey={ServiceKey} revisionId={RevisionId} violationCount={ViolationCount}",
                ServiceKeys.Build(identity),
                revisionId,
                admissionDecision.Violations.Count);
            await FailActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.AdmissionRejected,
                GetTerminalFailureReason(ServiceDeploymentActivationFailureCode.AdmissionRejected));
            return;
        }

        var existingActive = State.Deployments.Values.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal) &&
            x.Status == ServiceDeploymentStatus.Active &&
            !string.IsNullOrWhiteSpace(x.PrimaryActorId));
        if (existingActive != null &&
            !string.IsNullOrWhiteSpace(existingActive.ArtifactHash))
        {
            EnsureActiveDeploymentMatchesArtifact(existingActive, artifact);
            pending = await EnsureServingTargetDispatchPendingAsync(
                identity,
                revisionId,
                activationAttemptId,
                existingActive.DeploymentId,
                existingActive.PrimaryActorId);
            await DispatchServingTargetsAsync(
                identity,
                revisionId,
                activationAttemptId,
                pending,
                artifact);
            return;
        }

        pending = await MarkRuntimeActivationInvocationStartedAsync(
            identity,
            revisionId,
            activationAttemptId);

        ServiceRuntimeActivationResult activation;
        try
        {
            activation = await AwaitExternalWithinDeadlineAsync(
                pending,
                "service-runtime-activation",
                ct => _runtimeActivator.ActivateAsync(
                    new ServiceRuntimeActivationRequest(
                        identity.Clone(),
                        artifact,
                        revisionId,
                        Id,
                        capabilityView,
                        activationAttemptId,
                        pending.RuntimeActivationOperationId),
                    ct));
            ArgumentNullException.ThrowIfNull(activation);
            if (string.IsNullOrWhiteSpace(activation.DeploymentId) ||
                string.IsNullOrWhiteSpace(activation.PrimaryActorId))
            {
                throw new InvalidOperationException("Runtime activation returned an incomplete result.");
            }
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.RuntimeActivationFailed,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(identity, revisionId, activationAttemptId))
            return;

        pending = State.PendingActivations[revisionId];
        var now = Timestamp.FromDateTime(UtcNow);
        var activated = new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = activation.DeploymentId,
            RevisionId = revisionId,
            PrimaryActorId = activation.PrimaryActorId,
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = now.Clone(),
            ArtifactHash = NormalizeExpectedArtifactHash(artifact.ArtifactHash),
            ActivationAttemptId = activationAttemptId,
        };
        var dispatchPending = BuildServingTargetDispatchPendingEvent(
            identity,
            revisionId,
            activationAttemptId,
            activation.DeploymentId,
            activation.PrimaryActorId,
            pending,
            ResolveServingOperationSequence(pending),
            now.ToDateTime());
        await PersistDomainEventsAsync([activated, dispatchPending]);
        await DispatchServingTargetsAsync(
            identity,
            revisionId,
            activationAttemptId,
            State.PendingActivations[revisionId],
            artifact);
    }

    private async Task<ServiceDeploymentPendingActivationRecord> EnsurePendingActivationAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        string expectedArtifactHash)
    {
        if (State.PendingActivations.TryGetValue(revisionId, out var existing) &&
            ActivationAttemptsMatch(existing.ActivationAttemptId, activationAttemptId))
        {
            var existingArtifactHash = NormalizeExpectedArtifactHash(existing.ExpectedArtifactHash);
            if (string.IsNullOrEmpty(existingArtifactHash) &&
                !string.IsNullOrEmpty(expectedArtifactHash))
            {
                if (GetActivationPhase(existing) !=
                    ServiceDeploymentActivationPhase.ActivationPending)
                {
                    throw new InvalidOperationException(
                        $"Activation attempt '{activationAttemptId}' for revision '{revisionId}' cannot add an expected artifact hash after artifact validation has completed.");
                }

                var upgraded = existing.Clone();
                upgraded.ExpectedArtifactHash = expectedArtifactHash;
                await PersistDomainEventAsync(BuildDeferredEvent(
                    identity,
                    upgraded,
                    existing.LastRetryFailureCode,
                    UtcNow));
                return State.PendingActivations[revisionId];
            }

            if (!string.Equals(existingArtifactHash, expectedArtifactHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Activation attempt '{activationAttemptId}' for revision '{revisionId}' is already bound to a different expected artifact hash.");
            }

            return existing;
        }

        var nowUtc = UtcNow;
        await PersistDomainEventAsync(new ServiceDeploymentActivationDeferredEvent
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            DeadlineAt = Timestamp.FromDateTime(nowUtc + ActivationRetryBudget),
            DeferredAt = Timestamp.FromDateTime(nowUtc),
            ActivationAttemptId = activationAttemptId,
            StartedAt = Timestamp.FromDateTime(nowUtc),
            Phase = ServiceDeploymentActivationPhase.ActivationPending,
            ExpectedArtifactHash = expectedArtifactHash,
        });
        return State.PendingActivations[revisionId];
    }

    private async Task<bool> ResolveActivationDeactivationConflictAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingActivationRecord activation)
    {
        var deactivation = State.PendingDeactivations.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.RevisionId, activation.RevisionId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(activation.DeploymentId) &&
             string.Equals(candidate.DeploymentId, activation.DeploymentId, StringComparison.Ordinal)));
        if (deactivation == null)
            return true;

        if (GetDeactivationPhase(deactivation) ==
            ServiceDeploymentDeactivationPhase.ServingTargetRemovalPending)
        {
            await PersistDomainEventAsync(new ServiceDeploymentDeactivationSupersededEvent
            {
                Identity = identity.Clone(),
                DeploymentId = deactivation.DeploymentId,
                DeactivationOperationId = deactivation.DeactivationOperationId,
                SupersededByActivationAttemptId = activation.ActivationAttemptId,
                SupersededAt = Timestamp.FromDateTime(UtcNow),
            });
            return true;
        }

        await FailActivationAsync(
            identity,
            activation.RevisionId,
            activation.ActivationAttemptId,
            ServiceDeploymentActivationFailureCode.SupersededByDeactivation,
            GetTerminalFailureReason(
                ServiceDeploymentActivationFailureCode.SupersededByDeactivation));
        return false;
    }

    private async Task DeferOrFailRetryableActivationAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        ServiceDeploymentActivationFailureCode failureCode,
        Exception? exception = null)
    {
        if (!State.PendingActivations.TryGetValue(revisionId, out var pending) ||
            !ActivationAttemptsMatch(pending.ActivationAttemptId, activationAttemptId))
        {
            return;
        }

        if (exception != null)
        {
            Logger.LogWarning(
                "Service activation dependency failed and will be retried. serviceKey={ServiceKey} revisionId={RevisionId} failureCode={FailureCode} exceptionType={ExceptionType}",
                ServiceKeys.Build(identity),
                revisionId,
                failureCode,
                exception.GetType().Name);
        }

        if (IsActivationDeadlineReached(pending))
        {
            await FailActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                failureCode,
                GetTerminalFailureReason(failureCode));
            return;
        }

        if (pending.LastRetryFailureCode == failureCode)
            return;

        await PersistDomainEventAsync(BuildDeferredEvent(
            identity,
            pending,
            failureCode,
            UtcNow));
    }

    private async Task<ServiceDeploymentPendingActivationRecord> MarkRuntimeActivationInvocationStartedAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId)
    {
        if (!TryGetMatchingPending(revisionId, activationAttemptId, out var pending))
            throw new InvalidOperationException("Service activation is no longer pending.");

        var operationId = string.IsNullOrWhiteSpace(pending.RuntimeActivationOperationId)
            ? Guid.NewGuid().ToString("N")
            : pending.RuntimeActivationOperationId;
        await PersistDomainEventAsync(new ServiceDeploymentRuntimeActivationInvocationStartedEvent
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ActivationAttemptId = activationAttemptId,
            OperationId = operationId,
            InvocationCount = pending.RuntimeActivationInvocationCount + 1,
            DeadlineAt = pending.DeadlineAt?.Clone(),
            ActivationStartedAt = pending.StartedAt?.Clone(),
            InvocationStartedAt = Timestamp.FromDateTime(UtcNow),
        });
        return State.PendingActivations[revisionId];
    }

    private async Task<ServiceDeploymentPendingActivationRecord> EnsureServingTargetDispatchPendingAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        string deploymentId,
        string primaryActorId)
    {
        if (!TryGetMatchingPending(revisionId, activationAttemptId, out var pending))
            throw new InvalidOperationException("Service activation is no longer pending.");

        if ((GetActivationPhase(pending) is
                 ServiceDeploymentActivationPhase.ServingTargetDispatchPending or
                 ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted) &&
            string.Equals(pending.DeploymentId, deploymentId, StringComparison.Ordinal) &&
            string.Equals(pending.PrimaryActorId, primaryActorId, StringComparison.Ordinal) &&
            pending.ServingOperationSequence > 0 &&
            !string.IsNullOrWhiteSpace(pending.ServingTargetOperationId) &&
            !string.IsNullOrWhiteSpace(pending.ServingTargetCommandId))
        {
            return pending;
        }

        await PersistDomainEventAsync(BuildServingTargetDispatchPendingEvent(
            identity,
            revisionId,
            activationAttemptId,
            deploymentId,
            primaryActorId,
            pending,
            ResolveServingOperationSequence(pending),
            UtcNow));
        return State.PendingActivations[revisionId];
    }

    private static ServiceDeploymentServingTargetsDispatchPendingEvent BuildServingTargetDispatchPendingEvent(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        string deploymentId,
        string primaryActorId,
        ServiceDeploymentPendingActivationRecord pending,
        long operationSequence,
        DateTime preparedAtUtc) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            DeploymentId = deploymentId ?? string.Empty,
            PrimaryActorId = primaryActorId ?? string.Empty,
            ActivationAttemptId = activationAttemptId,
            OperationId = string.IsNullOrWhiteSpace(pending.ServingTargetOperationId)
                ? BuildServingOperationId(identity, deploymentId, operationSequence)
                : pending.ServingTargetOperationId,
            CommandId = string.IsNullOrWhiteSpace(pending.ServingTargetCommandId)
                ? BuildServingCommandId(identity, deploymentId, operationSequence)
                : pending.ServingTargetCommandId,
            DeadlineAt = pending.DeadlineAt?.Clone(),
            ActivationStartedAt = pending.StartedAt?.Clone(),
            PreparedAt = Timestamp.FromDateTime(preparedAtUtc),
            ExpectedArtifactHash = pending.ExpectedArtifactHash,
            OperationSequence = operationSequence,
        };

    private long ResolveServingOperationSequence(ServiceDeploymentPendingActivationRecord pending) =>
        pending.ServingOperationSequence > 0
            ? pending.ServingOperationSequence
            : checked(State.LastServingOperationSequence + 1);

    private async Task DispatchServingTargetsAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        ServiceDeploymentPendingActivationRecord pending,
        PreparedServiceRevisionArtifact artifact)
    {
        var deploymentId = pending.DeploymentId;
        var primaryActorId = pending.PrimaryActorId;
        if (string.IsNullOrWhiteSpace(deploymentId) || string.IsNullOrWhiteSpace(primaryActorId))
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
            return;
        }

        try
        {
            var admitted = await AwaitExternalWithinDeadlineAsync(
                pending,
                "serving-target-dispatch",
                ct => DispatchResolvedServingTargetsAsync(
                    identity,
                    revisionId,
                    deploymentId,
                    primaryActorId,
                    activationAttemptId,
                    pending.ServingTargetOperationId,
                    pending.ServingTargetCommandId,
                    pending.ServingOperationSequence,
                    artifact,
                    ct));
            if (!admitted)
            {
                await DeferOrFailRetryableActivationAsync(
                    identity,
                    revisionId,
                    activationAttemptId,
                    ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed);
                return;
            }
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(identity, revisionId, activationAttemptId))
            return;

        pending = State.PendingActivations[revisionId];
        if (!string.Equals(pending.DeploymentId, deploymentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(pending.ServingTargetOperationId) ||
            string.IsNullOrWhiteSpace(pending.ServingTargetCommandId))
        {
            return;
        }

        if (GetActivationPhase(pending) == ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted)
            return;

        await PersistDomainEventAsync(new ServiceDeploymentServingTargetsDispatchAcceptedEvent
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            DeploymentId = deploymentId,
            ActivationAttemptId = activationAttemptId,
            OperationId = pending.ServingTargetOperationId,
            CommandId = pending.ServingTargetCommandId,
            AcceptedAt = Timestamp.FromDateTime(UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleServingTargetsAppliedAsync(ServiceServingTargetsAppliedAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        EnsureDeploymentIdentity(ack.Identity, allowInitialize: false);
        if (ActiveInboundEnvelope != null &&
            !string.Equals(
                ActiveInboundEnvelope.Route?.PublisherActorId,
                ServiceActorIds.ServingSet(ack.Identity!),
                StringComparison.Ordinal))
        {
            return;
        }

        if (!TryGetMatchingPending(ack.RevisionId, ack.ActivationAttemptId, out var pending) ||
            GetActivationPhase(pending) is not (
                ServiceDeploymentActivationPhase.ServingTargetDispatchPending or
                ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted or
                ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending) ||
            !string.Equals(pending.DeploymentId, ack.DeploymentId, StringComparison.Ordinal) ||
            !string.Equals(pending.ServingTargetOperationId, ack.OperationId, StringComparison.Ordinal) ||
            pending.ServingOperationSequence != ack.OperationSequence)
        {
            return;
        }

        if (ack.Disposition == ServiceServingTargetsApplyDisposition.Superseded)
        {
            if (ack.SupersededByOperationSequence <= ack.OperationSequence)
                return;
            await FailActivationAsync(
                ack.Identity!,
                ack.RevisionId,
                ack.ActivationAttemptId,
                ServiceDeploymentActivationFailureCode.ServingTargetSuperseded,
                GetTerminalFailureReason(ServiceDeploymentActivationFailureCode.ServingTargetSuperseded));
            return;
        }
        if (ack.Disposition != ServiceServingTargetsApplyDisposition.Applied)
            return;

        // Success belongs to the deployment actor's inbox observation order. An ACK handled at
        // or after the actor-owned deadline loses to the timeout fence even if AppliedAt claims
        // an earlier wall-clock time from another node.
        if (!await EnsureMatchingPendingWithinDeadlineAsync(
                ack.Identity!,
                ack.RevisionId,
                ack.ActivationAttemptId))
        {
            return;
        }

        pending = State.PendingActivations[ack.RevisionId];
        if (GetActivationPhase(pending) ==
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending)
        {
            if (pending.ServingGeneration != ack.ServingGeneration)
                return;

            await DispatchDefaultServingRevisionAsync(
                ack.Identity!,
                ack.RevisionId,
                ack.ActivationAttemptId,
                pending);
            return;
        }

        if (ack.ServingGeneration <= 0)
            return;

        await PersistDomainEventAsync(new ServiceDeploymentDefaultServingRevisionDispatchPendingEvent
        {
            Identity = ack.Identity?.Clone(),
            RevisionId = ack.RevisionId,
            DeploymentId = ack.DeploymentId,
            ActivationAttemptId = ack.ActivationAttemptId,
            OperationId = ack.OperationId,
            CommandId = BuildDefaultServingEnvelopeId(ack.OperationId),
            ServingGeneration = ack.ServingGeneration,
            ServingTargetsAppliedAt = ack.AppliedAt?.Clone() ?? Timestamp.FromDateTime(UtcNow),
            PreparedAt = Timestamp.FromDateTime(UtcNow),
        });

        await DispatchDefaultServingRevisionAsync(
            ack.Identity!,
            ack.RevisionId,
            ack.ActivationAttemptId,
            State.PendingActivations[ack.RevisionId]);
    }

    [EventHandler]
    public async Task HandleDefaultServingRevisionCommittedAsync(
        DefaultServingRevisionCommittedAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        EnsureDeploymentIdentity(ack.Identity, allowInitialize: false);
        if (ActiveInboundEnvelope != null &&
            !string.Equals(
                ActiveInboundEnvelope.Route?.PublisherActorId,
                ServiceActorIds.Definition(ack.Identity!),
                StringComparison.Ordinal))
        {
            return;
        }

        if (!TryGetMatchingPending(ack.RevisionId, ack.ActivationAttemptId, out var pending) ||
            GetActivationPhase(pending) !=
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending ||
            !string.Equals(pending.DeploymentId, ack.DeploymentId, StringComparison.Ordinal) ||
            !string.Equals(pending.DefaultServingOperationId, ack.OperationId, StringComparison.Ordinal) ||
            !string.Equals(pending.DefaultServingCommandId, ack.CommandId, StringComparison.Ordinal) ||
            pending.ServingGeneration != ack.ServingGeneration ||
            ack.CommittedAt == null)
        {
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(
                ack.Identity!,
                ack.RevisionId,
                ack.ActivationAttemptId))
        {
            return;
        }

        if (ack.Disposition == DefaultServingRevisionCommitDisposition.Superseded)
        {
            if (ack.SupersededByGeneration <= ack.ServingGeneration)
                return;

            await FailActivationAsync(
                ack.Identity!,
                ack.RevisionId,
                ack.ActivationAttemptId,
                ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded,
                GetTerminalFailureReason(
                    ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded));
            return;
        }

        if (ack.Disposition != DefaultServingRevisionCommitDisposition.Applied ||
            ack.SupersededByGeneration != 0)
            return;

        await PersistDomainEventAsync(new ServiceDeploymentServingTargetsAppliedEvent
        {
            Identity = ack.Identity?.Clone(),
            RevisionId = ack.RevisionId,
            DeploymentId = ack.DeploymentId,
            ActivationAttemptId = ack.ActivationAttemptId,
            OperationId = pending.ServingTargetOperationId,
            ServingGeneration = pending.ServingGeneration,
            AppliedAt = pending.ServingTargetsAppliedAt?.Clone() ?? Timestamp.FromDateTime(UtcNow),
            DefaultServingCommittedAt = ack.CommittedAt.Clone(),
            OperationSequence = pending.ServingOperationSequence,
        });
    }

    private async Task DispatchDefaultServingRevisionAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        ServiceDeploymentPendingActivationRecord pending)
    {
        if (string.IsNullOrWhiteSpace(pending.DeploymentId) ||
            string.IsNullOrWhiteSpace(pending.DefaultServingOperationId) ||
            string.IsNullOrWhiteSpace(pending.DefaultServingCommandId) ||
            pending.ServingGeneration <= 0)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed);
            return;
        }

        try
        {
            var definitionActorId = ServiceActorIds.Definition(identity);
            var admission = await AwaitExternalWithinDeadlineAsync(
                pending,
                "default-serving-revision-dispatch",
                ct => _dispatchPort.DispatchAsync(
                    definitionActorId,
                    CreateEnvelope(
                        definitionActorId,
                        pending.DefaultServingCommandId,
                        new SetDefaultServingRevisionCommand
                        {
                            Identity = identity.Clone(),
                            RevisionId = revisionId,
                            OperationId = pending.DefaultServingOperationId,
                            CommandId = pending.DefaultServingCommandId,
                            ReplyActorId = Id,
                            ActivationAttemptId = activationAttemptId,
                            DeploymentId = pending.DeploymentId,
                            ServingGeneration = pending.ServingGeneration,
                        }),
                    ct));
            if (!admission.Accepted)
            {
                await DeferOrFailRetryableActivationAsync(
                    identity,
                    revisionId,
                    activationAttemptId,
                    ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed);
                return;
            }
        }
        catch (Exception exception)
        {
            await DeferOrFailRetryableActivationAsync(
                identity,
                revisionId,
                activationAttemptId,
                ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed,
                exception);
            return;
        }

        if (!await EnsureMatchingPendingWithinDeadlineAsync(identity, revisionId, activationAttemptId))
            return;

        pending = State.PendingActivations[revisionId];
        if (pending.DefaultServingDispatchAcceptedAt != null)
            return;

        await PersistDomainEventAsync(new ServiceDeploymentDefaultServingRevisionDispatchAcceptedEvent
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            DeploymentId = pending.DeploymentId,
            ActivationAttemptId = activationAttemptId,
            OperationId = pending.DefaultServingOperationId,
            CommandId = pending.DefaultServingCommandId,
            AcceptedAt = Timestamp.FromDateTime(UtcNow),
        });
    }

    private async Task ScheduleActivationRetryAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingActivationRecord pending,
        bool scheduleExpired,
        CancellationToken ct)
    {
        var nowUtc = UtcNow;
        var deadlineUtc = pending.DeadlineAt?.ToDateTime() ?? nowUtc;
        var remaining = deadlineUtc - nowUtc;
        if (remaining <= TimeSpan.Zero && !scheduleExpired)
            return;

        var dueTime = remaining <= TimeSpan.Zero
            ? ActivationRecoveryMinimumDelay
            : remaining < ActivationRetryInterval
                ? remaining
                : ActivationRetryInterval;
        var retryCommand = new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = pending.RevisionId,
            ActivationDeadlineAt = pending.DeadlineAt?.Clone() ?? Timestamp.FromDateTime(deadlineUtc),
            ActivationAttemptId = pending.ActivationAttemptId,
            ExpectedArtifactHash = pending.ExpectedArtifactHash,
        };

        Logger.LogInformation(
            "Service activation retry armed. serviceKey={ServiceKey} revisionId={RevisionId} phase={Phase} dueSeconds={DueSeconds:F1} deadline={Deadline:O}",
            ServiceKeys.Build(identity),
            pending.RevisionId,
            GetActivationPhase(pending),
            dueTime.TotalSeconds,
            deadlineUtc);
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                $"{ActivationRetryCallbackPrefix}:{pending.RevisionId}",
                dueTime,
                retryCommand,
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ServiceDeploymentActivationRetrySchedulePendingException(
                "The service activation pending record is durable, but its recovery callback could not be scheduled.",
                exception);
        }
    }

    private async Task ScheduleDeactivationRetryAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingDeactivationRecord pending,
        bool scheduleExpired,
        CancellationToken ct)
    {
        var nowUtc = UtcNow;
        var deadlineUtc = pending.DeadlineAt?.ToDateTime() ?? nowUtc;
        var remaining = deadlineUtc - nowUtc;
        if (remaining <= TimeSpan.Zero && !scheduleExpired)
            return;

        var dueTime = remaining <= TimeSpan.Zero
            ? DeactivationRecoveryMinimumDelay
            : remaining < DeactivationRetryInterval
                ? remaining
                : DeactivationRetryInterval;
        var retryCommand = new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = pending.DeploymentId,
            DeactivationDeadlineAt = pending.DeadlineAt?.Clone()
                                     ?? Timestamp.FromDateTime(deadlineUtc),
            DeactivationOperationId = pending.DeactivationOperationId,
        };

        Logger.LogInformation(
            "Service deactivation retry armed. serviceKey={ServiceKey} deploymentId={DeploymentId} phase={Phase} dueSeconds={DueSeconds:F1} deadline={Deadline:O}",
            ServiceKeys.Build(identity),
            pending.DeploymentId,
            GetDeactivationPhase(pending),
            dueTime.TotalSeconds,
            deadlineUtc);
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                $"{DeactivationRetryCallbackPrefix}:{pending.DeploymentId}",
                dueTime,
                retryCommand,
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ServiceDeploymentDeactivationRetrySchedulePendingException(
                "The service deactivation pending record is durable, but its recovery callback could not be scheduled.",
                exception);
        }
    }

    private async Task DeferOrFailDeactivationAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingDeactivationRecord pending,
        ServiceDeploymentDeactivationFailureCode failureCode,
        string failureReason,
        Exception? exception = null)
    {
        if (!TryGetMatchingPendingDeactivation(
                pending.DeploymentId,
                pending.DeactivationOperationId,
                out var current))
        {
            return;
        }

        if (exception != null)
        {
            Logger.LogWarning(
                "Service deactivation dependency failed and will be retried. serviceKey={ServiceKey} deploymentId={DeploymentId} failureCode={FailureCode} exceptionType={ExceptionType}",
                ServiceKeys.Build(identity),
                current.DeploymentId,
                failureCode,
                exception.GetType().Name);
        }

        if (IsDeactivationDeadlineReached(current))
        {
            await FailDeactivationAsync(
                identity,
                current,
                failureCode,
                GetDeactivationTerminalFailureReason(failureCode));
            return;
        }

        var deferred = current.Clone();
        deferred.DeferredAt = Timestamp.FromDateTime(UtcNow);
        deferred.LastFailureCode = failureCode;
        deferred.LastFailureReason = failureReason?.Trim() ?? string.Empty;
        await PersistDomainEventAsync(new ServiceDeploymentDeactivationDeferredEvent
        {
            Identity = identity.Clone(),
            Deactivation = deferred,
        });
    }

    private async Task FailDeactivationAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingDeactivationRecord pending,
        ServiceDeploymentDeactivationFailureCode failureCode,
        string failureReason)
    {
        if (!TryGetMatchingPendingDeactivation(
                pending.DeploymentId,
                pending.DeactivationOperationId,
                out var current))
        {
            return;
        }

        if (State.DeactivationFailures.TryGetValue(current.DeploymentId, out var existing) &&
            string.Equals(
                existing.DeactivationOperationId,
                current.DeactivationOperationId,
                StringComparison.Ordinal) &&
            existing.FailureCode == failureCode &&
            string.Equals(existing.FailureReason, failureReason, StringComparison.Ordinal))
        {
            return;
        }

        Logger.LogError(
            "Service deactivation failed terminally. serviceKey={ServiceKey} deploymentId={DeploymentId} failureCode={FailureCode} failureReason={FailureReason}",
            ServiceKeys.Build(identity),
            current.DeploymentId,
            failureCode,
            failureReason);
        await PersistDomainEventAsync(new ServiceDeploymentDeactivationFailedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = current.DeploymentId,
            RevisionId = current.RevisionId,
            DeactivationOperationId = current.DeactivationOperationId,
            ActivationAttemptId = current.ActivationAttemptId,
            ServingTargetOperationId = current.ServingTargetOperationId,
            FailureCode = failureCode,
            FailureReason = failureReason,
            OccurredAt = Timestamp.FromDateTime(UtcNow),
        });
    }

    private bool IsDeactivationDeadlineReached(
        ServiceDeploymentPendingDeactivationRecord pending) =>
        pending.DeadlineAt == null || UtcNow >= pending.DeadlineAt.ToDateTime();

    private static ServiceDeploymentDeactivationPhase GetDeactivationPhase(
        ServiceDeploymentPendingDeactivationRecord pending) =>
        pending.Phase == ServiceDeploymentDeactivationPhase.Unspecified
            ? ServiceDeploymentDeactivationPhase.ServingTargetRemovalPending
            : pending.Phase;

    private static ServiceDeploymentDeactivationFailureCode GetDeactivationDeadlineFailureCode(
        ServiceDeploymentPendingDeactivationRecord pending) =>
        pending.LastFailureCode != ServiceDeploymentDeactivationFailureCode.Unspecified
            ? pending.LastFailureCode
            : GetDeactivationPhase(pending) ==
              ServiceDeploymentDeactivationPhase.RuntimeDeactivationPending
                ? ServiceDeploymentDeactivationFailureCode.RuntimeDeactivationFailed
                : ServiceDeploymentDeactivationFailureCode.ServingTargetRemovalFailed;

    private static string GetDeactivationTerminalFailureReason(
        ServiceDeploymentDeactivationFailureCode failureCode) =>
        failureCode switch
        {
            ServiceDeploymentDeactivationFailureCode.ServingTargetRemovalFailed =>
                "Service serving target removal did not complete before the deactivation deadline.",
            ServiceDeploymentDeactivationFailureCode.RuntimeDeactivationFailed =>
                "Service runtime deactivation did not complete before the deactivation deadline.",
            _ => "Service deactivation failed.",
        };

    private static ServiceDeploymentActivationDeferredEvent BuildDeferredEvent(
        ServiceIdentity identity,
        ServiceDeploymentPendingActivationRecord pending,
        ServiceDeploymentActivationFailureCode failureCode,
        DateTime deferredAtUtc) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = pending.RevisionId,
            DeadlineAt = pending.DeadlineAt?.Clone(),
            DeferredAt = Timestamp.FromDateTime(deferredAtUtc),
            ActivationAttemptId = pending.ActivationAttemptId,
            StartedAt = pending.StartedAt?.Clone(),
            Phase = GetActivationPhase(pending),
            DeploymentId = pending.DeploymentId,
            PrimaryActorId = pending.PrimaryActorId,
            LastRetryFailureCode = failureCode,
            RuntimeActivationOperationId = pending.RuntimeActivationOperationId,
            RuntimeActivationInvocationCount = pending.RuntimeActivationInvocationCount,
            RuntimeActivationInvocationStartedAt = pending.RuntimeActivationInvocationStartedAt?.Clone(),
            ServingTargetOperationId = pending.ServingTargetOperationId,
            ServingTargetCommandId = pending.ServingTargetCommandId,
            ServingTargetDispatchAcceptedAt = pending.ServingTargetDispatchAcceptedAt?.Clone(),
            ExpectedArtifactHash = pending.ExpectedArtifactHash,
            DefaultServingOperationId = pending.DefaultServingOperationId,
            DefaultServingCommandId = pending.DefaultServingCommandId,
            DefaultServingDispatchAcceptedAt = pending.DefaultServingDispatchAcceptedAt?.Clone(),
            ServingGeneration = pending.ServingGeneration,
            ServingTargetsAppliedAt = pending.ServingTargetsAppliedAt?.Clone(),
            ServingOperationSequence = pending.ServingOperationSequence,
        };

    private bool IsActivationDeadlineReached(ServiceDeploymentPendingActivationRecord pending) =>
        pending.DeadlineAt == null || UtcNow >= pending.DeadlineAt.ToDateTime();

    private static ServiceDeploymentActivationFailureCode GetDeadlineFailureCode(
        ServiceDeploymentPendingActivationRecord pending)
    {
        if (pending.LastRetryFailureCode != ServiceDeploymentActivationFailureCode.Unspecified)
            return pending.LastRetryFailureCode;

        return GetActivationPhase(pending) switch
        {
            ServiceDeploymentActivationPhase.RuntimeActivationInvocationPending =>
                ServiceDeploymentActivationFailureCode.RuntimeActivationFailed,
            ServiceDeploymentActivationPhase.ServingTargetDispatchPending or
                ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted =>
                ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed,
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending =>
                ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed,
            _ => ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable,
        };
    }

    private static ServiceDeploymentActivationPhase GetActivationPhase(
        ServiceDeploymentPendingActivationRecord pending)
    {
        if (pending.Phase != ServiceDeploymentActivationPhase.Unspecified)
            return pending.Phase;

        return !string.IsNullOrWhiteSpace(pending.DeploymentId) &&
               !string.IsNullOrWhiteSpace(pending.PrimaryActorId)
            ? ServiceDeploymentActivationPhase.ServingTargetDispatchPending
            : ServiceDeploymentActivationPhase.ActivationPending;
    }

    private static string GetTerminalFailureReason(ServiceDeploymentActivationFailureCode failureCode) =>
        failureCode switch
        {
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing =>
                "Prepared artifact was not found before the activation deadline.",
            ServiceDeploymentActivationFailureCode.RevisionPreparationFailed =>
                "Service revision preparation failed.",
            ServiceDeploymentActivationFailureCode.CapabilityViewNotReady =>
                "Required service-catalog projection or activation-capability-view projection was not ready before the activation deadline.",
            ServiceDeploymentActivationFailureCode.AdmissionRejected =>
                "Service activation admission was rejected.",
            ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed =>
                "Service activation admission could not be evaluated before the activation deadline.",
            ServiceDeploymentActivationFailureCode.RuntimeActivationFailed =>
                "Service runtime activation did not complete before the activation deadline.",
            ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed =>
                "Service serving target delivery did not complete before the activation deadline.",
            ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed =>
                "Default serving revision did not commit before the activation deadline.",
            ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded =>
                "Default serving revision was superseded by a newer serving generation.",
            ServiceDeploymentActivationFailureCode.SupersededByDeactivation =>
                "Service activation was superseded by an accepted deactivation operation.",
            ServiceDeploymentActivationFailureCode.ServingTargetSuperseded =>
                "Service serving target update was superseded by a newer operation.",
            ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable =>
                "A required service activation dependency remained unavailable until the activation deadline.",
            _ => "Service activation failed.",
        };

    private async Task FailActivationAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId,
        ServiceDeploymentActivationFailureCode failureCode,
        string failureReason)
    {
        var hasActiveDeployment = State.Deployments.Values.Any(x =>
                string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal) &&
                x.Status == ServiceDeploymentStatus.Active);
        var hasMatchingPending = State.PendingActivations.TryGetValue(revisionId, out var pending) &&
                                 ActivationAttemptsMatch(pending.ActivationAttemptId, activationAttemptId);
        if (hasActiveDeployment && !hasMatchingPending)
        {
            return;
        }

        if (State.ActivationFailures.TryGetValue(revisionId, out var existing) &&
            ActivationAttemptsMatch(existing.ActivationAttemptId, activationAttemptId) &&
            !string.IsNullOrEmpty(activationAttemptId) &&
            existing.FailureCode == failureCode &&
            string.Equals(existing.FailureReason, failureReason, StringComparison.Ordinal))
        {
            return;
        }

        Logger.LogError(
            "Service activation failed terminally. serviceKey={ServiceKey} revisionId={RevisionId} failureCode={FailureCode} failureReason={FailureReason}",
            ServiceKeys.Build(identity),
            revisionId,
            failureCode,
            failureReason);
        await PersistDomainEventAsync(new ServiceDeploymentActivationFailedEvent
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            FailureCode = failureCode,
            FailureReason = failureReason,
            OccurredAt = Timestamp.FromDateTime(UtcNow),
            ActivationAttemptId = activationAttemptId,
        });
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeactivateAsync(DeactivateServiceDeploymentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: false);
        if (string.IsNullOrWhiteSpace(command.DeploymentId))
            throw new InvalidOperationException("deployment_id is required.");

        var requestedOperationId = command.DeactivationOperationId?.Trim() ?? string.Empty;
        var isContinuation = command.DeactivationDeadlineAt != null ||
                             !string.IsNullOrEmpty(requestedOperationId);
        if (State.PendingDeactivations.TryGetValue(command.DeploymentId, out var pending))
        {
            if (!string.IsNullOrEmpty(requestedOperationId) &&
                !string.Equals(
                    pending.DeactivationOperationId,
                    requestedOperationId,
                    StringComparison.Ordinal))
            {
                return;
            }
        }
        else
        {
            if (isContinuation ||
                !State.Deployments.TryGetValue(command.DeploymentId, out var deployment) ||
                string.IsNullOrWhiteSpace(deployment.PrimaryActorId) ||
                deployment.Status != ServiceDeploymentStatus.Active)
            {
                return;
            }

            foreach (var activation in State.PendingActivations.Values
                         .Where(candidate =>
                             string.Equals(
                                 candidate.DeploymentId,
                                 deployment.DeploymentId,
                                 StringComparison.Ordinal) ||
                             string.Equals(
                                 candidate.RevisionId,
                                 deployment.RevisionId,
                                 StringComparison.Ordinal))
                         .ToArray())
            {
                await FailActivationAsync(
                    command.Identity,
                    activation.RevisionId,
                    activation.ActivationAttemptId,
                    ServiceDeploymentActivationFailureCode.SupersededByDeactivation,
                    GetTerminalFailureReason(
                        ServiceDeploymentActivationFailureCode.SupersededByDeactivation));
            }

            var nowUtc = UtcNow;
            var operationId = string.IsNullOrEmpty(requestedOperationId)
                ? Guid.NewGuid().ToString("N")
                : requestedOperationId;
            pending = new ServiceDeploymentPendingDeactivationRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                ActivationAttemptId = deployment.ActivationAttemptId,
                ServingTargetOperationId = deployment.ServingTargetOperationId,
                DeactivationOperationId = operationId,
                RemovalCommandId = BuildRemovalEnvelopeId(operationId),
                Phase = ServiceDeploymentDeactivationPhase.ServingTargetRemovalPending,
                DeadlineAt = command.DeactivationDeadlineAt?.Clone()
                             ?? Timestamp.FromDateTime(nowUtc + DeactivationRetryBudget),
                StartedAt = Timestamp.FromDateTime(nowUtc),
                DeferredAt = Timestamp.FromDateTime(nowUtc),
            };
            await PersistDomainEventAsync(new ServiceDeploymentDeactivationRequestedEvent
            {
                Identity = command.Identity.Clone(),
                Deactivation = pending.Clone(),
            });
            pending = State.PendingDeactivations[command.DeploymentId];
        }

        await ScheduleDeactivationRetryAsync(
            command.Identity,
            pending,
            scheduleExpired: false,
            ct: CancellationToken.None);
        if (IsDeactivationDeadlineReached(pending))
        {
            var failureCode = GetDeactivationDeadlineFailureCode(pending);
            await FailDeactivationAsync(
                command.Identity,
                pending,
                failureCode,
                GetDeactivationTerminalFailureReason(failureCode));
            return;
        }

        if (GetDeactivationPhase(pending) ==
            ServiceDeploymentDeactivationPhase.RuntimeDeactivationPending)
        {
            await ExecuteRuntimeDeactivationAsync(command.Identity, pending);
            return;
        }

        await DispatchServingTargetRemovalAsync(command.Identity, pending);
    }

    [EventHandler]
    public async Task HandleServingTargetsRemovedAsync(ServiceServingTargetsRemovedAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        EnsureDeploymentIdentity(ack.Identity, allowInitialize: false);
        if (ActiveInboundEnvelope != null &&
            !string.Equals(
                ActiveInboundEnvelope.Route?.PublisherActorId,
                ServiceActorIds.ServingSet(ack.Identity!),
                StringComparison.Ordinal))
        {
            return;
        }

        if (!State.PendingDeactivations.TryGetValue(ack.DeploymentId, out var pending) ||
            !MatchesDeactivationAck(pending, ack))
        {
            return;
        }

        if (ack.Disposition == ServiceServingTargetRemovalDisposition.Superseded)
        {
            await PersistDomainEventAsync(new ServiceDeploymentDeactivationSupersededEvent
            {
                Identity = ack.Identity!.Clone(),
                DeploymentId = pending.DeploymentId,
                DeactivationOperationId = pending.DeactivationOperationId,
                SupersededByActivationAttemptId = ack.ActualActivationAttemptId,
                SupersededAt = Timestamp.FromDateTime(UtcNow),
            });
            return;
        }

        if (ack.Disposition is not (
                ServiceServingTargetRemovalDisposition.Removed or
                ServiceServingTargetRemovalDisposition.AlreadyAbsent))
        {
            return;
        }

        if (GetDeactivationPhase(pending) ==
            ServiceDeploymentDeactivationPhase.ServingTargetRemovalPending)
        {
            await PersistDomainEventAsync(
                new ServiceDeploymentRuntimeDeactivationInvocationStartedEvent
                {
                    Identity = ack.Identity!.Clone(),
                    DeploymentId = pending.DeploymentId,
                    DeactivationOperationId = pending.DeactivationOperationId,
                    InvocationCount = pending.RuntimeDeactivationInvocationCount + 1,
                    InvocationStartedAt = Timestamp.FromDateTime(UtcNow),
                    RemovalObservedAt = ack.RemovedAt?.Clone()
                                        ?? Timestamp.FromDateTime(UtcNow),
                });
            pending = State.PendingDeactivations[ack.DeploymentId];
        }

        await ExecuteRuntimeDeactivationAsync(ack.Identity!, pending);
    }

    private async Task DispatchServingTargetRemovalAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingDeactivationRecord pending)
    {
        try
        {
            var servingActorId = ServiceActorIds.ServingSet(identity);
            var admission = await AwaitExternalWithinDeadlineAsync(
                pending.DeadlineAt,
                "serving-target-removal-dispatch",
                ct => _dispatchPort.DispatchAsync(
                    servingActorId,
                    CreateEnvelope(
                        servingActorId,
                        pending.RemovalCommandId,
                        new RemoveDeploymentFromServiceServingTargetsCommand
                        {
                            Identity = identity.Clone(),
                            DeploymentId = pending.DeploymentId,
                            RevisionId = pending.RevisionId,
                            PrimaryActorId = pending.PrimaryActorId,
                            ActivationAttemptId = pending.ActivationAttemptId,
                            ServingTargetOperationId = pending.ServingTargetOperationId,
                            DeactivationOperationId = pending.DeactivationOperationId,
                            ReplyActorId = Id,
                            Reason = $"deactivate:{pending.DeploymentId}",
                        }),
                    ct));
            if (!admission.Accepted)
            {
                await DeferOrFailDeactivationAsync(
                    identity,
                    pending,
                    ServiceDeploymentDeactivationFailureCode.ServingTargetRemovalFailed,
                    "Service serving target removal was not admitted.");
                return;
            }

            if (!TryGetMatchingPendingDeactivation(
                    pending.DeploymentId,
                    pending.DeactivationOperationId,
                    out var current) ||
                current.RemovalDispatchAcceptedAt != null)
            {
                return;
            }

            var accepted = current.Clone();
            accepted.RemovalDispatchAcceptedAt = Timestamp.FromDateTime(UtcNow);
            accepted.DeferredAt = Timestamp.FromDateTime(UtcNow);
            accepted.LastFailureCode = ServiceDeploymentDeactivationFailureCode.Unspecified;
            accepted.LastFailureReason = string.Empty;
            await PersistDomainEventAsync(new ServiceDeploymentDeactivationDeferredEvent
            {
                Identity = identity.Clone(),
                Deactivation = accepted,
            });
        }
        catch (Exception exception)
        {
            await DeferOrFailDeactivationAsync(
                identity,
                pending,
                ServiceDeploymentDeactivationFailureCode.ServingTargetRemovalFailed,
                exception.Message,
                exception);
        }
    }

    private async Task ExecuteRuntimeDeactivationAsync(
        ServiceIdentity identity,
        ServiceDeploymentPendingDeactivationRecord pending)
    {
        if (!TryGetMatchingPendingDeactivation(
                pending.DeploymentId,
                pending.DeactivationOperationId,
                out pending))
        {
            return;
        }

        if (IsDeactivationDeadlineReached(pending))
        {
            await FailDeactivationAsync(
                identity,
                pending,
                ServiceDeploymentDeactivationFailureCode.RuntimeDeactivationFailed,
                GetDeactivationTerminalFailureReason(
                    ServiceDeploymentDeactivationFailureCode.RuntimeDeactivationFailed));
            return;
        }

        try
        {
            await AwaitExternalWithinDeadlineAsync(
                pending.DeadlineAt,
                "service-runtime-deactivation",
                async ct =>
                {
                    await _runtimeActivator.DeactivateAsync(
                        new ServiceRuntimeDeactivationRequest(
                            identity.Clone(),
                            pending.DeploymentId,
                            pending.RevisionId,
                            pending.PrimaryActorId,
                            pending.DeactivationOperationId),
                        ct);
                    return true;
                });
        }
        catch (Exception exception)
        {
            await DeferOrFailDeactivationAsync(
                identity,
                pending,
                ServiceDeploymentDeactivationFailureCode.RuntimeDeactivationFailed,
                exception.Message,
                exception);
            return;
        }

        if (!TryGetMatchingPendingDeactivation(
                pending.DeploymentId,
                pending.DeactivationOperationId,
                out var current))
        {
            return;
        }

        await PersistDomainEventAsync(new ServiceDeploymentDeactivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = current.DeploymentId,
            RevisionId = current.RevisionId,
            DeactivatedAt = Timestamp.FromDateTime(UtcNow),
            DeactivationOperationId = current.DeactivationOperationId,
            ActivationAttemptId = current.ActivationAttemptId,
            ServingTargetOperationId = current.ServingTargetOperationId,
        });
    }

    private static bool MatchesDeactivationAck(
        ServiceDeploymentPendingDeactivationRecord pending,
        ServiceServingTargetsRemovedAck ack) =>
        string.Equals(
            pending.DeactivationOperationId,
            ack.DeactivationOperationId,
            StringComparison.Ordinal) &&
        string.Equals(pending.RevisionId, ack.RevisionId, StringComparison.Ordinal) &&
        string.Equals(pending.PrimaryActorId, ack.PrimaryActorId, StringComparison.Ordinal) &&
        string.Equals(pending.ActivationAttemptId, ack.ActivationAttemptId, StringComparison.Ordinal) &&
        string.Equals(
            pending.ServingTargetOperationId,
            ack.ServingTargetOperationId,
            StringComparison.Ordinal);

    protected override ServiceDeploymentState TransitionState(ServiceDeploymentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceDeploymentActivatedEvent>(ApplyActivated)
            .On<ServiceDeploymentDeactivationRequestedEvent>(ApplyDeactivationRequested)
            .On<ServiceDeploymentDeactivationDeferredEvent>(ApplyDeactivationDeferred)
            .On<ServiceDeploymentRuntimeDeactivationInvocationStartedEvent>(
                ApplyRuntimeDeactivationInvocationStarted)
            .On<ServiceDeploymentDeactivationSupersededEvent>(ApplyDeactivationSuperseded)
            .On<ServiceDeploymentDeactivationFailedEvent>(ApplyDeactivationFailed)
            .On<ServiceDeploymentDeactivatedEvent>(ApplyDeactivated)
            .On<ServiceDeploymentHealthChangedEvent>(ApplyHealthChanged)
            .On<ServiceDeploymentActivationFailedEvent>(ApplyActivationFailed)
            .On<ServiceDeploymentActivationDeferredEvent>(ApplyActivationDeferred)
            .On<ServiceDeploymentRuntimeActivationInvocationStartedEvent>(ApplyRuntimeActivationInvocationStarted)
            .On<ServiceDeploymentServingTargetsDispatchPendingEvent>(ApplyServingTargetsDispatchPending)
            .On<ServiceDeploymentServingTargetsDispatchAcceptedEvent>(ApplyServingTargetsDispatchAccepted)
            .On<ServiceDeploymentDefaultServingRevisionDispatchPendingEvent>(ApplyDefaultServingRevisionDispatchPending)
            .On<ServiceDeploymentDefaultServingRevisionDispatchAcceptedEvent>(ApplyDefaultServingRevisionDispatchAccepted)
            .On<ServiceDeploymentServingTargetsAppliedEvent>(ApplyServingTargetsApplied)
            .OrCurrent();

    private static ServiceDeploymentState ApplyActivated(ServiceDeploymentState state, ServiceDeploymentActivatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.Deployments[evt.DeploymentId] = new ServiceDeploymentRecord
        {
            DeploymentId = evt.DeploymentId ?? string.Empty,
            RevisionId = evt.RevisionId ?? string.Empty,
            PrimaryActorId = evt.PrimaryActorId ?? string.Empty,
            Status = evt.Status,
            ActivatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            ArtifactHash = NormalizeExpectedArtifactHash(evt.ArtifactHash),
            ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty,
        };
        // Preserve the legacy reducer contract: an activated event always closes the
        // activation pending record. New code atomically follows it with an explicit
        // serving-target dispatch checkpoint when delivery still needs confirmation.
        next.PendingActivations.Remove(evt.RevisionId);
        next.ActivationFailures.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "activated");
        return next;
    }

    private static ServiceDeploymentState ApplyActivationFailed(
        ServiceDeploymentState state,
        ServiceDeploymentActivationFailedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        next.ActivationFailures[evt.RevisionId] = new ServiceDeploymentActivationFailureRecord
        {
            RevisionId = evt.RevisionId ?? string.Empty,
            FailureCode = evt.FailureCode,
            FailureReason = evt.FailureReason ?? string.Empty,
            OccurredAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty,
        };
        next.PendingActivations.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "activation-failed");
        return next;
    }

    private static ServiceDeploymentState ApplyActivationDeferred(
        ServiceDeploymentState state,
        ServiceDeploymentActivationDeferredEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        state.PendingActivations.TryGetValue(evt.RevisionId, out var existing);
        var matchingExisting = existing != null &&
                               ActivationAttemptsMatch(existing.ActivationAttemptId, evt.ActivationAttemptId)
            ? existing
            : null;
        next.PendingActivations[evt.RevisionId] = CreatePendingActivationRecord(evt, matchingExisting);
        next.ActivationFailures.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "activation-deferred");
        return next;
    }

    private static ServiceDeploymentPendingActivationRecord CreatePendingActivationRecord(
        ServiceDeploymentActivationDeferredEvent evt,
        ServiceDeploymentPendingActivationRecord? existing) =>
        new()
        {
            RevisionId = evt.RevisionId ?? string.Empty,
            DeadlineAt = FirstTimestamp(evt.DeadlineAt, existing?.DeadlineAt),
            DeferredAt = FirstTimestamp(evt.DeferredAt),
            ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty,
            StartedAt = FirstTimestamp(evt.StartedAt, existing?.StartedAt, evt.DeferredAt),
            Phase = ResolveActivationPhase(evt.Phase, existing),
            DeploymentId = FirstNonBlank(evt.DeploymentId, existing?.DeploymentId),
            PrimaryActorId = FirstNonBlank(evt.PrimaryActorId, existing?.PrimaryActorId),
            LastRetryFailureCode = evt.LastRetryFailureCode,
            RuntimeActivationOperationId = FirstNonBlank(
                evt.RuntimeActivationOperationId,
                existing?.RuntimeActivationOperationId),
            RuntimeActivationInvocationCount = evt.RuntimeActivationInvocationCount != 0
                ? evt.RuntimeActivationInvocationCount
                : existing?.RuntimeActivationInvocationCount ?? 0,
            RuntimeActivationInvocationStartedAt = FirstOptionalTimestamp(
                evt.RuntimeActivationInvocationStartedAt,
                existing?.RuntimeActivationInvocationStartedAt),
            ServingTargetOperationId = FirstNonBlank(
                evt.ServingTargetOperationId,
                existing?.ServingTargetOperationId),
            ServingTargetCommandId = FirstNonBlank(
                evt.ServingTargetCommandId,
                existing?.ServingTargetCommandId),
            ServingTargetDispatchAcceptedAt = FirstOptionalTimestamp(
                evt.ServingTargetDispatchAcceptedAt,
                existing?.ServingTargetDispatchAcceptedAt),
            ExpectedArtifactHash = FirstNonBlank(
                evt.ExpectedArtifactHash,
                existing?.ExpectedArtifactHash),
            DefaultServingOperationId = FirstNonBlank(
                evt.DefaultServingOperationId,
                existing?.DefaultServingOperationId),
            DefaultServingCommandId = FirstNonBlank(
                evt.DefaultServingCommandId,
                existing?.DefaultServingCommandId),
            DefaultServingDispatchAcceptedAt = FirstOptionalTimestamp(
                evt.DefaultServingDispatchAcceptedAt,
                existing?.DefaultServingDispatchAcceptedAt),
            ServingGeneration = evt.ServingGeneration != 0
                ? evt.ServingGeneration
                : existing?.ServingGeneration ?? 0,
            ServingTargetsAppliedAt = FirstOptionalTimestamp(
                evt.ServingTargetsAppliedAt,
                existing?.ServingTargetsAppliedAt),
            ServingOperationSequence = evt.ServingOperationSequence != 0
                ? evt.ServingOperationSequence
                : existing?.ServingOperationSequence ?? 0,
        };

    private static Timestamp FirstTimestamp(
        Timestamp? first,
        Timestamp? second = null,
        Timestamp? third = null,
        Timestamp? fourth = null) =>
        first?.Clone()
        ?? second?.Clone()
        ?? third?.Clone()
        ?? fourth?.Clone()
        ?? Timestamp.FromDateTime(DateTime.UnixEpoch);

    private static Timestamp? FirstOptionalTimestamp(Timestamp? first, Timestamp? second = null) =>
        first?.Clone() ?? second?.Clone();

    private static ServiceDeploymentActivationPhase ResolveActivationPhase(
        ServiceDeploymentActivationPhase phase,
        ServiceDeploymentPendingActivationRecord? existing)
    {
        if (phase != ServiceDeploymentActivationPhase.Unspecified)
            return phase;

        return existing == null
            ? ServiceDeploymentActivationPhase.ActivationPending
            : GetActivationPhase(existing);
    }

    private static string FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;

    private static ServiceDeploymentState ApplyRuntimeActivationInvocationStarted(
        ServiceDeploymentState state,
        ServiceDeploymentRuntimeActivationInvocationStartedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingActivations.TryGetValue(evt.RevisionId, out var pending) &&
            ActivationAttemptsMatch(pending.ActivationAttemptId, evt.ActivationAttemptId) &&
            (string.IsNullOrWhiteSpace(pending.RuntimeActivationOperationId) ||
             string.Equals(pending.RuntimeActivationOperationId, evt.OperationId, StringComparison.Ordinal)))
        {
            var invocationPending = pending.Clone();
            invocationPending.Phase = ServiceDeploymentActivationPhase.RuntimeActivationInvocationPending;
            invocationPending.RuntimeActivationOperationId = evt.OperationId ?? string.Empty;
            invocationPending.RuntimeActivationInvocationCount = evt.InvocationCount;
            invocationPending.RuntimeActivationInvocationStartedAt = evt.InvocationStartedAt?.Clone();
            invocationPending.DeadlineAt = FirstTimestamp(evt.DeadlineAt, pending.DeadlineAt);
            invocationPending.StartedAt = FirstTimestamp(evt.ActivationStartedAt, pending.StartedAt);
            invocationPending.DeferredAt = FirstTimestamp(
                evt.InvocationStartedAt,
                pending.DeferredAt,
                pending.StartedAt);
            invocationPending.LastRetryFailureCode = ServiceDeploymentActivationFailureCode.Unspecified;
            next.PendingActivations[evt.RevisionId] = invocationPending;
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RevisionId, "runtime-activation-invoked");
        return next;
    }

    private static ServiceDeploymentState ApplyServingTargetsDispatchPending(
        ServiceDeploymentState state,
        ServiceDeploymentServingTargetsDispatchPendingEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        next.PendingActivations[evt.RevisionId] = new ServiceDeploymentPendingActivationRecord
        {
            RevisionId = evt.RevisionId ?? string.Empty,
            DeadlineAt = FirstTimestamp(evt.DeadlineAt),
            DeferredAt = FirstTimestamp(evt.PreparedAt, evt.ActivationStartedAt),
            ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty,
            StartedAt = FirstTimestamp(evt.ActivationStartedAt, evt.PreparedAt),
            Phase = ServiceDeploymentActivationPhase.ServingTargetDispatchPending,
            DeploymentId = evt.DeploymentId ?? string.Empty,
            PrimaryActorId = evt.PrimaryActorId ?? string.Empty,
            LastRetryFailureCode = ServiceDeploymentActivationFailureCode.Unspecified,
            ServingTargetOperationId = evt.OperationId ?? string.Empty,
            ServingTargetCommandId = evt.CommandId ?? string.Empty,
            ExpectedArtifactHash = evt.ExpectedArtifactHash ?? string.Empty,
            ServingOperationSequence = evt.OperationSequence,
        };
        next.LastServingOperationSequence = Math.Max(
            next.LastServingOperationSequence,
            evt.OperationSequence);
        if (next.Deployments.TryGetValue(evt.DeploymentId, out var deployment))
        {
            var fencedDeployment = deployment.Clone();
            fencedDeployment.ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty;
            fencedDeployment.ServingTargetOperationId = evt.OperationId ?? string.Empty;
            next.Deployments[evt.DeploymentId] = fencedDeployment;
        }
        next.ActivationFailures.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "serving-targets-dispatch-pending");
        return next;
    }

    private static ServiceDeploymentState ApplyServingTargetsDispatchAccepted(
        ServiceDeploymentState state,
        ServiceDeploymentServingTargetsDispatchAcceptedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingActivations.TryGetValue(evt.RevisionId, out var pending) &&
            ActivationAttemptsMatch(pending.ActivationAttemptId, evt.ActivationAttemptId) &&
            string.Equals(pending.DeploymentId, evt.DeploymentId, StringComparison.Ordinal) &&
            string.Equals(pending.ServingTargetOperationId, evt.OperationId, StringComparison.Ordinal) &&
            string.Equals(pending.ServingTargetCommandId, evt.CommandId, StringComparison.Ordinal))
        {
            var accepted = pending.Clone();
            accepted.Phase = ServiceDeploymentActivationPhase.ServingTargetDispatchAccepted;
            accepted.ServingTargetDispatchAcceptedAt = FirstTimestamp(
                evt.AcceptedAt,
                pending.DeferredAt,
                pending.StartedAt);
            accepted.LastRetryFailureCode = ServiceDeploymentActivationFailureCode.Unspecified;
            next.PendingActivations[evt.RevisionId] = accepted;
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "serving-targets-dispatch-accepted");
        return next;
    }

    private static ServiceDeploymentState ApplyDefaultServingRevisionDispatchPending(
        ServiceDeploymentState state,
        ServiceDeploymentDefaultServingRevisionDispatchPendingEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingActivations.TryGetValue(evt.RevisionId, out var pending) &&
            ActivationAttemptsMatch(pending.ActivationAttemptId, evt.ActivationAttemptId) &&
            string.Equals(pending.DeploymentId, evt.DeploymentId, StringComparison.Ordinal) &&
            string.Equals(pending.ServingTargetOperationId, evt.OperationId, StringComparison.Ordinal))
        {
            var defaultServingPending = pending.Clone();
            defaultServingPending.Phase =
                ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending;
            defaultServingPending.DefaultServingOperationId = evt.OperationId ?? string.Empty;
            defaultServingPending.DefaultServingCommandId = evt.CommandId ?? string.Empty;
            defaultServingPending.DefaultServingDispatchAcceptedAt = null;
            defaultServingPending.ServingGeneration = evt.ServingGeneration;
            defaultServingPending.ServingTargetsAppliedAt = evt.ServingTargetsAppliedAt?.Clone();
            defaultServingPending.DeferredAt = FirstTimestamp(
                evt.PreparedAt,
                pending.DeferredAt,
                pending.StartedAt);
            defaultServingPending.LastRetryFailureCode =
                ServiceDeploymentActivationFailureCode.Unspecified;
            next.PendingActivations[evt.RevisionId] = defaultServingPending;
        }

        next.ActivationFailures.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.DeploymentId,
            "default-serving-dispatch-pending");
        return next;
    }

    private static ServiceDeploymentState ApplyDefaultServingRevisionDispatchAccepted(
        ServiceDeploymentState state,
        ServiceDeploymentDefaultServingRevisionDispatchAcceptedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingActivations.TryGetValue(evt.RevisionId, out var pending) &&
            ActivationAttemptsMatch(pending.ActivationAttemptId, evt.ActivationAttemptId) &&
            GetActivationPhase(pending) ==
            ServiceDeploymentActivationPhase.DefaultServingRevisionDispatchPending &&
            string.Equals(pending.DeploymentId, evt.DeploymentId, StringComparison.Ordinal) &&
            string.Equals(pending.DefaultServingOperationId, evt.OperationId, StringComparison.Ordinal) &&
            string.Equals(pending.DefaultServingCommandId, evt.CommandId, StringComparison.Ordinal))
        {
            var accepted = pending.Clone();
            accepted.DefaultServingDispatchAcceptedAt = FirstTimestamp(
                evt.AcceptedAt,
                pending.DeferredAt,
                pending.StartedAt);
            accepted.LastRetryFailureCode = ServiceDeploymentActivationFailureCode.Unspecified;
            next.PendingActivations[evt.RevisionId] = accepted;
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.DeploymentId,
            "default-serving-dispatch-accepted");
        return next;
    }

    private static ServiceDeploymentState ApplyServingTargetsApplied(
        ServiceDeploymentState state,
        ServiceDeploymentServingTargetsAppliedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingActivations.TryGetValue(evt.RevisionId, out var pending) &&
            ActivationAttemptsMatch(pending.ActivationAttemptId, evt.ActivationAttemptId) &&
            string.Equals(pending.DeploymentId, evt.DeploymentId, StringComparison.Ordinal) &&
            string.Equals(pending.ServingTargetOperationId, evt.OperationId, StringComparison.Ordinal))
        {
            if (next.Deployments.TryGetValue(evt.DeploymentId, out var deployment))
            {
                var activated = deployment.Clone();
                activated.ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty;
                activated.ServingTargetOperationId = evt.OperationId ?? string.Empty;
                next.Deployments[evt.DeploymentId] = activated;
            }

            next.PendingActivations.Remove(evt.RevisionId);
            if (!string.IsNullOrWhiteSpace(evt.ActivationAttemptId))
            {
                next.ActivationCompletions[BuildCompletionKey(evt.RevisionId, evt.ActivationAttemptId)] =
                    new ServiceDeploymentActivationCompletionRecord
                    {
                        RevisionId = evt.RevisionId ?? string.Empty,
                        DeploymentId = evt.DeploymentId ?? string.Empty,
                        ActivationAttemptId = evt.ActivationAttemptId ?? string.Empty,
                        ServingTargetOperationId = evt.OperationId ?? string.Empty,
                        ServingGeneration = evt.ServingGeneration,
                        DefaultServingOperationId = pending.DefaultServingOperationId,
                        DefaultServingCommandId = pending.DefaultServingCommandId,
                        DefaultServingCommittedAt = evt.DefaultServingCommittedAt?.Clone(),
                        ExpectedArtifactHash = NormalizeExpectedArtifactHash(
                            pending.ExpectedArtifactHash),
                        ServingOperationSequence = pending.ServingOperationSequence,
                        CompletedAt = FirstTimestamp(
                            evt.DefaultServingCommittedAt,
                            evt.AppliedAt,
                            pending.ServingTargetDispatchAcceptedAt,
                            pending.DeferredAt),
                    };
            }
        }

        next.ActivationFailures.Remove(evt.RevisionId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "serving-targets-applied");
        return next;
    }

    private bool ShouldIgnoreFencedActivation(
        string revisionId,
        string activationAttemptId,
        string expectedArtifactHash,
        bool isContinuation)
    {
        if (isContinuation)
        {
            return !State.PendingActivations.TryGetValue(revisionId, out var pending) ||
                   !ActivationAttemptsMatch(pending.ActivationAttemptId, activationAttemptId) ||
                   !string.Equals(
                       pending.ExpectedArtifactHash,
                       expectedArtifactHash,
                       StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(activationAttemptId) &&
            State.ActivationCompletions.TryGetValue(
                BuildCompletionKey(revisionId, activationAttemptId),
                out var completion))
        {
            if (!string.Equals(
                    NormalizeExpectedArtifactHash(completion.ExpectedArtifactHash),
                    expectedArtifactHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Activation attempt '{activationAttemptId}' for revision '{revisionId}' is already bound to a different expected artifact hash.");
            }

            return true;
        }

        if (!State.ActivationFailures.TryGetValue(revisionId, out var failure))
            return false;

        if (!ActivationAttemptsMatch(failure.ActivationAttemptId, activationAttemptId))
            return false;

        // Non-empty attempt ids identify the same logical request even when the caller replays
        // the original command. Empty ids retain the legacy external-retry behavior.
        return !string.IsNullOrEmpty(activationAttemptId);
    }

    private static bool ActivationAttemptsMatch(string? left, string? right) =>
        string.Equals(
            NormalizeActivationAttemptId(left),
            NormalizeActivationAttemptId(right),
            StringComparison.Ordinal);

    private static string NormalizeActivationAttemptId(string? activationAttemptId) =>
        activationAttemptId?.Trim() ?? string.Empty;

    private static string NormalizeExpectedArtifactHash(string? expectedArtifactHash) =>
        expectedArtifactHash?.Trim() ?? string.Empty;

    private static void EnsureActiveDeploymentMatchesArtifact(
        ServiceDeploymentRecord deployment,
        PreparedServiceRevisionArtifact artifact)
    {
        var activeArtifactHash = NormalizeExpectedArtifactHash(deployment.ArtifactHash);
        var requestedArtifactHash = NormalizeExpectedArtifactHash(artifact.ArtifactHash);
        if (string.IsNullOrEmpty(requestedArtifactHash) ||
            !string.Equals(activeArtifactHash, requestedArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Active deployment '{deployment.DeploymentId}' for revision '{deployment.RevisionId}' cannot be reused because its artifact hash does not match the published prepared artifact.");
        }
    }

    private static string BuildCompletionKey(string? revisionId, string? activationAttemptId) =>
        $"{revisionId?.Trim() ?? string.Empty}\n{NormalizeActivationAttemptId(activationAttemptId)}";

    private static ServiceDeploymentState ApplyDeactivationRequested(
        ServiceDeploymentState state,
        ServiceDeploymentDeactivationRequestedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (evt.Deactivation != null &&
            !string.IsNullOrWhiteSpace(evt.Deactivation.DeploymentId) &&
            !string.IsNullOrWhiteSpace(evt.Deactivation.DeactivationOperationId))
        {
            var pending = evt.Deactivation.Clone();
            if (pending.Phase == ServiceDeploymentDeactivationPhase.Unspecified)
            {
                pending.Phase =
                    ServiceDeploymentDeactivationPhase.ServingTargetRemovalPending;
            }

            next.PendingDeactivations[pending.DeploymentId] = pending;
            next.DeactivationFailures.Remove(pending.DeploymentId);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.Deactivation?.DeploymentId,
            "deactivation-requested");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivationDeferred(
        ServiceDeploymentState state,
        ServiceDeploymentDeactivationDeferredEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (evt.Deactivation != null &&
            state.PendingDeactivations.TryGetValue(
                evt.Deactivation.DeploymentId,
                out var existing) &&
            string.Equals(
                existing.DeactivationOperationId,
                evt.Deactivation.DeactivationOperationId,
                StringComparison.Ordinal))
        {
            next.PendingDeactivations[evt.Deactivation.DeploymentId] =
                evt.Deactivation.Clone();
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.Deactivation?.DeploymentId,
            "deactivation-deferred");
        return next;
    }

    private static ServiceDeploymentState ApplyRuntimeDeactivationInvocationStarted(
        ServiceDeploymentState state,
        ServiceDeploymentRuntimeDeactivationInvocationStartedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingDeactivations.TryGetValue(evt.DeploymentId, out var pending) &&
            string.Equals(
                pending.DeactivationOperationId,
                evt.DeactivationOperationId,
                StringComparison.Ordinal))
        {
            var invoked = pending.Clone();
            invoked.Phase = ServiceDeploymentDeactivationPhase.RuntimeDeactivationPending;
            invoked.RuntimeDeactivationInvocationCount = evt.InvocationCount;
            invoked.RuntimeDeactivationInvocationStartedAt = evt.InvocationStartedAt?.Clone();
            invoked.RemovalObservedAt = evt.RemovalObservedAt?.Clone();
            invoked.DeferredAt = evt.InvocationStartedAt?.Clone()
                                 ?? pending.DeferredAt?.Clone();
            invoked.LastFailureCode = ServiceDeploymentDeactivationFailureCode.Unspecified;
            invoked.LastFailureReason = string.Empty;
            next.PendingDeactivations[evt.DeploymentId] = invoked;
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.DeploymentId,
            "runtime-deactivation-invoked");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivationSuperseded(
        ServiceDeploymentState state,
        ServiceDeploymentDeactivationSupersededEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingDeactivations.TryGetValue(evt.DeploymentId, out var pending) &&
            string.Equals(
                pending.DeactivationOperationId,
                evt.DeactivationOperationId,
                StringComparison.Ordinal))
        {
            next.PendingDeactivations.Remove(evt.DeploymentId);
        }

        next.DeactivationFailures.Remove(evt.DeploymentId);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.DeploymentId,
            "deactivation-superseded");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivationFailed(
        ServiceDeploymentState state,
        ServiceDeploymentDeactivationFailedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.PendingDeactivations.TryGetValue(evt.DeploymentId, out var pending) &&
            string.Equals(
                pending.DeactivationOperationId,
                evt.DeactivationOperationId,
                StringComparison.Ordinal))
        {
            next.PendingDeactivations.Remove(evt.DeploymentId);
            next.DeactivationFailures[evt.DeploymentId] =
                new ServiceDeploymentDeactivationFailureRecord
                {
                    DeploymentId = evt.DeploymentId,
                    RevisionId = evt.RevisionId,
                    DeactivationOperationId = evt.DeactivationOperationId,
                    FailureCode = evt.FailureCode,
                    FailureReason = evt.FailureReason,
                    OccurredAt = evt.OccurredAt?.Clone()
                                 ?? Timestamp.FromDateTime(DateTime.UtcNow),
                };
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(
            evt.Identity,
            evt.DeploymentId,
            "deactivation-failed");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivated(
        ServiceDeploymentState state,
        ServiceDeploymentDeactivatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        var ownsPending = next.PendingDeactivations.TryGetValue(evt.DeploymentId, out var pending) &&
                          string.Equals(
                              pending.DeactivationOperationId,
                              evt.DeactivationOperationId,
                              StringComparison.Ordinal);
        if (ownsPending &&
            next.Deployments.TryGetValue(evt.DeploymentId, out var deployment) &&
            DeploymentMatchesDeactivationFence(
                deployment,
                evt.ActivationAttemptId,
                evt.ServingTargetOperationId))
        {
            var deactivated = deployment.Clone();
            deactivated.Status = ServiceDeploymentStatus.Deactivated;
            deactivated.UpdatedAt = evt.DeactivatedAt?.Clone()
                                    ?? Timestamp.FromDateTime(DateTime.UtcNow);
            next.Deployments[evt.DeploymentId] = deactivated;
        }

        if (ownsPending)
            next.PendingDeactivations.Remove(evt.DeploymentId);
        next.DeactivationFailures.Remove(evt.DeploymentId);

        foreach (var completionKey in next.ActivationCompletions
                     .Where(entry =>
                         string.Equals(
                             entry.Value.DeploymentId,
                             evt.DeploymentId,
                             StringComparison.Ordinal) &&
                         string.Equals(
                             entry.Value.ActivationAttemptId,
                             evt.ActivationAttemptId,
                             StringComparison.Ordinal) &&
                         string.Equals(
                             entry.Value.ServingTargetOperationId,
                             evt.ServingTargetOperationId,
                             StringComparison.Ordinal))
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            next.ActivationCompletions.Remove(completionKey);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "deactivated");
        return next;
    }

    private static bool DeploymentMatchesDeactivationFence(
        ServiceDeploymentRecord deployment,
        string? activationAttemptId,
        string? servingTargetOperationId) =>
        string.Equals(
            deployment.ActivationAttemptId,
            activationAttemptId,
            StringComparison.Ordinal) &&
        string.Equals(
            deployment.ServingTargetOperationId,
            servingTargetOperationId,
            StringComparison.Ordinal);

    private static ServiceDeploymentState ApplyHealthChanged(ServiceDeploymentState state, ServiceDeploymentHealthChangedEvent evt)
    {
        var next = state.Clone();
        if (next.Deployments.TryGetValue(evt.DeploymentId, out var deployment))
        {
            next.Deployments[evt.DeploymentId] = new ServiceDeploymentRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                Status = evt.Status,
                ActivatedAt = deployment.ActivatedAt?.Clone(),
                UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
                ArtifactHash = deployment.ArtifactHash,
                ActivationAttemptId = deployment.ActivationAttemptId,
                ServingTargetOperationId = deployment.ServingTargetOperationId,
            };
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "health");
        return next;
    }

    private void EnsureDeploymentIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service deployment '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service deployment actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string? deploymentId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{deploymentId ?? "none"}:{suffix}";
    }

    private bool TryGetMatchingPending(
        string revisionId,
        string activationAttemptId,
        out ServiceDeploymentPendingActivationRecord pending)
    {
        if (State.PendingActivations.TryGetValue(revisionId, out var current) &&
            ActivationAttemptsMatch(current.ActivationAttemptId, activationAttemptId))
        {
            pending = current;
            return true;
        }

        pending = null!;
        return false;
    }

    private bool TryGetMatchingPendingDeactivation(
        string deploymentId,
        string deactivationOperationId,
        out ServiceDeploymentPendingDeactivationRecord pending)
    {
        if (State.PendingDeactivations.TryGetValue(deploymentId, out var current) &&
            string.Equals(
                current.DeactivationOperationId,
                deactivationOperationId,
                StringComparison.Ordinal))
        {
            pending = current;
            return true;
        }

        pending = null!;
        return false;
    }

    private async Task<bool> EnsureMatchingPendingWithinDeadlineAsync(
        ServiceIdentity identity,
        string revisionId,
        string activationAttemptId)
    {
        if (!TryGetMatchingPending(revisionId, activationAttemptId, out var pending))
            return false;
        if (!IsActivationDeadlineReached(pending))
            return true;

        var failureCode = GetDeadlineFailureCode(pending);
        await FailActivationAsync(
            identity,
            revisionId,
            activationAttemptId,
            failureCode,
            GetTerminalFailureReason(failureCode));
        return false;
    }

    private Task<T> AwaitExternalWithinDeadlineAsync<T>(
        ServiceDeploymentPendingActivationRecord pending,
        string operationName,
        Func<CancellationToken, Task<T>> invoke) =>
        AwaitExternalWithinDeadlineAsync(pending.DeadlineAt, operationName, invoke);

    private async Task<T> AwaitExternalWithinDeadlineAsync<T>(
        Timestamp? deadline,
        string operationName,
        Func<CancellationToken, Task<T>> invoke)
    {
        var deadlineUtc = deadline?.ToDateTime() ?? UtcNow;
        var remaining = deadlineUtc - UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"External operation '{operationName}' exceeded its deadline.");

        CancellationTokenSource? timeoutCts = new();
        Task<T>? dependencyTask = null;
        try
        {
            dependencyTask = invoke(timeoutCts.Token);
            return await dependencyTask.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException) when (dependencyTask is { IsCompleted: false })
        {
            var timedOutCts = timeoutCts;
            timeoutCts = null;
            CancelTimedOutExternalTask(timedOutCts, Logger, operationName);
            ObserveLateExternalTask(dependencyTask, Logger, operationName);
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static void CancelTimedOutExternalTask(
        CancellationTokenSource cancellation,
        ILogger logger,
        string operationName)
    {
        _ = Task.Run(() =>
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "External dependency cancellation callback failed after its deadline. operation={Operation} exceptionType={ExceptionType}",
                    operationName,
                    exception.GetType().Name);
            }
            finally
            {
                cancellation.Dispose();
            }
        });
    }

    private static void ObserveLateExternalTask<T>(Task<T> task, ILogger logger, string operationName)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var observation = ((ILogger Logger, string OperationName))state!;
                if (completed.IsFaulted)
                {
                    observation.Logger.LogWarning(
                        "Late external dependency task faulted after its deadline. operation={Operation} exceptionType={ExceptionType}",
                        observation.OperationName,
                        completed.Exception?.GetBaseException().GetType().Name ?? "Unknown");
                }
                else
                {
                    observation.Logger.LogDebug(
                        "Late external dependency task completed after its deadline. operation={Operation} status={Status}",
                        observation.OperationName,
                        completed.Status);
                }
            },
            (logger, operationName),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<bool> DispatchResolvedServingTargetsAsync(
        ServiceIdentity identity,
        string revisionId,
        string deploymentId,
        string primaryActorId,
        string activationAttemptId,
        string operationId,
        string commandId,
        long operationSequence,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct)
    {
        var actorId = ServiceActorIds.ServingSet(identity);
        var admission = await _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                commandId,
                new ReplaceResolvedServiceServingTargetsCommand
                {
                    Identity = identity.Clone(),
                    Reason = "deployment activation",
                    ActivationAttemptId = activationAttemptId,
                    OperationId = operationId,
                    OperationSequence = operationSequence,
                    ReplyActorId = Id,
                    Targets =
                    {
                        new ServiceServingTargetSpec
                        {
                            DeploymentId = deploymentId ?? string.Empty,
                            RevisionId = revisionId ?? string.Empty,
                            PrimaryActorId = primaryActorId ?? string.Empty,
                            AllocationWeight = 100,
                            ServingState = ServiceServingState.Active,
                            EnabledEndpointIds = { artifact.Endpoints.Select(x => x.EndpointId) },
                        },
                    },
                }),
            ct);
        return admission.Accepted;
    }

    private EventEnvelope CreateEnvelope(string actorId, string envelopeId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(Id, actorId),
            Propagation = new EnvelopePropagation(),
        };
    }

    private static string BuildDefaultServingEnvelopeId(string? servingOperationId) =>
        $"service-deployment-default-serving:{servingOperationId?.Trim() ?? string.Empty}";

    private static string BuildServingOperationId(
        ServiceIdentity identity,
        string deploymentId,
        long operationSequence) =>
        $"service-serving-operation:{ServiceKeys.Build(identity)}:{deploymentId}:{operationSequence}";

    private static string BuildServingCommandId(
        ServiceIdentity identity,
        string deploymentId,
        long operationSequence) =>
        $"service-serving-command:{ServiceKeys.Build(identity)}:{deploymentId}:{operationSequence}";

    private static string BuildRemovalEnvelopeId(string deactivationOperationId) =>
        $"service-deployment-remove-serving:{deactivationOperationId}";

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

}
