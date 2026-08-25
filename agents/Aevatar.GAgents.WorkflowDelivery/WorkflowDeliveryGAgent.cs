using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.WorkflowDelivery;

[GAgent("workflow.delivery")]
public sealed class WorkflowDeliveryGAgent
    : GAgentBase<WorkflowDeliveryState>, IProjectedActor
{
    private static readonly TimeSpan MaximumContinuationClaimDuration = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider;

    public WorkflowDeliveryGAgent(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string ProjectionKind => "workflow-delivery";

    [EventHandler(EndpointName = "createWorkflowDelivery")]
    public async Task HandleCreateAsync(CreateWorkflowDeliveryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCreate(command);
        EnsureCanonicalActor(command.DeliveryId);

        if (State.DeliveryId.Length != 0)
        {
            if (SameCreate(State, command))
                return;
            throw new InvalidOperationException("workflow delivery already exists with different immutable content.");
        }

        await PersistDomainEventAsync(new WorkflowDeliveryCreatedEvent
        {
            DeliveryId = command.DeliveryId.Trim(),
            Package = command.Package.Clone(),
            TargetScopeId = command.TargetScopeId.Trim(),
            ExpiresAtUtc = command.ExpiresAtUtc.Clone(),
            ExpiresAtDefaulted = command.ExpiresAtDefaulted,
            Note = command.Note?.Trim() ?? string.Empty,
            CreatedBy = command.CreatedBy.Trim(),
            CreatedAtUtc = command.CreatedAtUtc.Clone(),
        });
    }

    [EventHandler(EndpointName = "recordWorkflowDeliveryAccess")]
    public async Task HandleAccessAsync(RecordWorkflowDeliveryAccessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        EnsureTargetScope(command.TargetScopeId);
        EnsureAvailable(command.AccessedAtUtc);
        if (State.AccessedAtUtc != null && State.AccessedAtUtc >= command.AccessedAtUtc)
            return;
        await PersistDomainEventAsync(new WorkflowDeliveryAccessRecordedEvent
        {
            AccessedAtUtc = RequireTimestamp(command.AccessedAtUtc, "accessed_at_utc").Clone(),
        });
    }

    [EventHandler(EndpointName = "revokeWorkflowDelivery")]
    public async Task HandleRevokeAsync(RevokeWorkflowDeliveryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var revokedBy = WorkflowDeliveryConventions.NormalizeRequired(command.RevokedBy, "revoked_by");
        var revokedAt = RequireTimestamp(command.RevokedAtUtc, "revoked_at_utc");
        if (State.LifecycleStatus == WorkflowDeliveryLifecycleStatus.Revoked)
        {
            if (string.Equals(State.RevokedBy, revokedBy, StringComparison.Ordinal))
                return;
            throw new InvalidOperationException("workflow delivery is already revoked by another principal.");
        }
        await PersistDomainEventAsync(new WorkflowDeliveryRevokedEvent
        {
            RevokedBy = revokedBy,
            RevokedAtUtc = revokedAt.Clone(),
        });
    }

    [EventHandler(EndpointName = "beginWorkflowDeliveryConnection")]
    public async Task HandleBeginConnectionAsync(BeginWorkflowDeliveryConnectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        EnsureTargetScope(command.TargetScopeId);
        EnsureAvailable(command.RequestedAtUtc);
        var slot = FindSlot(command.SlotKey, command.ServiceSlug);
        var existing = State.Connections.SingleOrDefault(x =>
            string.Equals(x.SlotKey, slot.Key, StringComparison.Ordinal));
        if (existing != null)
        {
            if (string.Equals(existing.LinkId, command.LinkId, StringComparison.Ordinal))
                return;
        }
        EnsureConnectionsMutable();
        if (existing?.Status == WorkflowDeliveryConnectionStatus.Pending)
            throw new InvalidOperationException("a connection link is already pending for this slot.");
        await PersistDomainEventAsync(new WorkflowDeliveryConnectionBegunEvent
        {
            SlotKey = slot.Key,
            ServiceSlug = slot.ServiceSlug,
            LinkId = WorkflowDeliveryConventions.NormalizeRequired(command.LinkId, "link_id"),
            RequestedAtUtc = RequireTimestamp(command.RequestedAtUtc, "requested_at_utc").Clone(),
        });
    }

    [EventHandler(EndpointName = "updateWorkflowDeliveryConnection")]
    public async Task HandleUpdateConnectionAsync(UpdateWorkflowDeliveryConnectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        EnsureTargetScope(command.TargetScopeId);
        var connection = State.Connections.SingleOrDefault(x =>
            string.Equals(x.SlotKey, command.SlotKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("workflow delivery connection slot has not been started.");
        if (!string.Equals(connection.LinkId, command.LinkId, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow delivery connection link does not match the active slot link.");
        if (command.Status == WorkflowDeliveryConnectionStatus.Unspecified)
            throw new InvalidOperationException("connection status is required.");
        var userServiceId = command.UserServiceId?.Trim() ?? string.Empty;
        if (command.Status == WorkflowDeliveryConnectionStatus.Completed && userServiceId.Length == 0)
            throw new InvalidOperationException("completed connection requires user_service_id.");
        if (connection.Status == command.Status &&
            string.Equals(connection.UserServiceId, userServiceId, StringComparison.Ordinal))
            return;
        EnsureConnectionsMutable();
        await PersistDomainEventAsync(new WorkflowDeliveryConnectionUpdatedEvent
        {
            SlotKey = connection.SlotKey,
            LinkId = connection.LinkId,
            Status = command.Status,
            UserServiceId = userServiceId,
            UpdatedAtUtc = RequireTimestamp(command.UpdatedAtUtc, "updated_at_utc").Clone(),
        });
    }

    [EventHandler(EndpointName = "attachWorkflowDeliveryConnection")]
    public async Task HandleAttachConnectionAsync(AttachWorkflowDeliveryConnectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        EnsureTargetScope(command.TargetScopeId);
        EnsureAvailable(command.AttachedAtUtc);
        var slot = FindSlot(command.SlotKey, command.ServiceSlug);
        var userServiceId = WorkflowDeliveryConventions.NormalizeRequired(
            command.UserServiceId,
            "user_service_id");
        var existing = State.Connections.SingleOrDefault(connection =>
            string.Equals(connection.SlotKey, slot.Key, StringComparison.Ordinal));
        if (existing?.Status == WorkflowDeliveryConnectionStatus.Completed &&
            string.Equals(existing.ServiceSlug, slot.ServiceSlug, StringComparison.Ordinal) &&
            string.Equals(existing.UserServiceId, userServiceId, StringComparison.Ordinal))
        {
            return;
        }

        EnsureConnectionsMutable();
        if (existing?.Status == WorkflowDeliveryConnectionStatus.Pending)
            throw new InvalidOperationException("a connection link is already pending for this slot.");
        EnsureAttachExpectedStateVersion(command.ExpectedStateVersion);

        await PersistDomainEventAsync(new WorkflowDeliveryConnectionAttachedEvent
        {
            SlotKey = slot.Key,
            ServiceSlug = slot.ServiceSlug,
            UserServiceId = userServiceId,
            AttachedAtUtc = RequireTimestamp(command.AttachedAtUtc, "attached_at_utc").Clone(),
        });
    }

    private void EnsureAttachExpectedStateVersion(long expectedStateVersion)
    {
        if (expectedStateVersion <= 0)
        {
            throw new InvalidOperationException(
                "workflow delivery attach expected_state_version must be positive.");
        }

        var currentVersion = EventSourcing?.CurrentVersion
            ?? throw new InvalidOperationException("workflow delivery event sourcing is unavailable.");
        if (expectedStateVersion != currentVersion)
        {
            throw new InvalidOperationException(
                $"workflow delivery attach expected_state_version {expectedStateVersion} " +
                $"does not match committed state version {currentVersion}.");
        }
    }

    [EventHandler(EndpointName = "startWorkflowInstallation")]
    public async Task HandleStartInstallationAsync(StartWorkflowInstallationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        EnsureTargetScope(command.ScopeId);
        EnsureAvailable(command.RequestedAtUtc);
        ValidateInstallation(command);

        if (State.Installation != null && State.Installation.InstallationId.Length != 0)
        {
            if (SameInstallation(State.Installation, command))
                return;
            throw new InvalidOperationException("workflow delivery already has a different installation.");
        }

        var acceptanceInput = WorkflowDeliveryConventions.ResolveAcceptanceInput(
            State.Package.AcceptancePolicy.Input,
            command.RequestedAtUtc,
            command.AuthenticatedOwner?.SubjectExternalUserId);
        var installation = new WorkflowInstallationState
        {
            InstallationId = command.InstallationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            ScopeId = command.ScopeId.Trim(),
            TeamId = command.TeamId.Trim(),
            TriggerIntent = command.TriggerIntent.Clone(),
            SourceHash = command.SourceHash.Trim(),
            ResolvedHash = command.ResolvedHash.Trim(),
            ResolvedYaml = command.ResolvedYaml,
            Status = WorkflowInstallationStatus.Accepted,
            Stage = "accepted",
            Attempt = 1,
            OperationId = WorkflowDeliveryConventions.NormalizeRequired(command.OperationId, "operation_id"),
            CapabilityAdmissionPlan = command.CapabilityAdmissionPlan.Clone(),
            AuthenticatedOwner = command.AuthenticatedOwner?.Clone(),
            AcceptanceInput = acceptanceInput,
            CreatedAtUtc = command.RequestedAtUtc.Clone(),
            UpdatedAtUtc = command.RequestedAtUtc.Clone(),
        };
        installation.ConfigurationValues.Add(command.ConfigurationValues);
        installation.ConnectionReferences.Add(command.ConnectionReferences);
        installation.Confirmations.Add(command.Confirmations.Select(static value => value.Clone()));
        await PersistDomainEventAsync(new WorkflowInstallationStartedEvent { Installation = installation });
    }

    [EventHandler(EndpointName = "retryWorkflowInstallation")]
    public async Task HandleRetryInstallationAsync(RetryWorkflowInstallationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var installation = RequireInstallation(command.InstallationId);
        if (!string.Equals(installation.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("installation idempotency key does not match.");
        if (installation.Status != WorkflowInstallationStatus.Failed)
            return;
        EnsureAvailable(command.RequestedAtUtc);
        var operationId = WorkflowDeliveryConventions.NormalizeRequired(command.OperationId, "operation_id");
        if (string.Equals(installation.OperationId, operationId, StringComparison.Ordinal))
            throw new InvalidOperationException("a retry must use a new provisioning operation identity.");
        await PersistDomainEventAsync(new WorkflowInstallationRetryRequestedEvent
        {
            RequestedAtUtc = RequireTimestamp(command.RequestedAtUtc, "requested_at_utc").Clone(),
            OperationId = operationId,
        });
    }

    [EventHandler(EndpointName = "claimWorkflowInstallationContinuation")]
    public async Task HandleClaimInstallationContinuationAsync(
        ClaimWorkflowInstallationContinuationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var installation = RequireInstallation(command.InstallationId);
        var actorNow = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var claim = NormalizeContinuationClaim(command, actorNow);
        if (claim.Attempt < installation.Attempt)
            return;
        if (claim.Attempt > installation.Attempt)
            throw new InvalidOperationException("continuation claim attempt is ahead of the active installation attempt.");
        if (!string.Equals(installation.OperationId, claim.OperationId, StringComparison.Ordinal))
            throw new InvalidOperationException("continuation claim operation identity does not match the active attempt.");
        if (installation.Status != claim.ExpectedStatus)
            return;

        var withdrawalReason = ResolveWithdrawalReason(actorNow);
        if (withdrawalReason != WorkflowInstallationWithdrawalReason.Unspecified)
        {
            await PersistDomainEventAsync(new WorkflowInstallationWithdrawnEvent
            {
                Reason = withdrawalReason,
                WithdrawnAtUtc = actorNow,
                Attempt = installation.Attempt,
                OperationId = installation.OperationId,
                FailedFromStatus = installation.Status,
            });
            return;
        }

        EnsureAvailable(actorNow);

        var existing = installation.ContinuationClaim;
        if (existing != null)
        {
            if (SameContinuationClaimIdentity(existing, claim))
                return;
            if (ContinuationClaimIsActiveFor(existing, installation, actorNow))
                return;
        }

        await PersistDomainEventAsync(new WorkflowInstallationContinuationClaimedEvent
        {
            Claim = claim,
        });
    }

    [EventHandler(EndpointName = "recordWorkflowProvisioningAccepted")]
    public async Task HandleProvisioningAcceptedAsync(RecordWorkflowProvisioningAcceptedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var actorNow = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var installation = RequireInstallation(command.InstallationId);
        if (!AcceptsOutcome(installation, command.Attempt, command.OperationId))
            return;
        var accepted = NormalizeProvisioningAccepted(command);
        if (installation.Status == WorkflowInstallationStatus.Ready)
        {
            if (SameProvisioning(installation, accepted))
                return;
            throw new InvalidOperationException("installation already accepted different provisioning identities.");
        }
        if (installation.Status == WorkflowInstallationStatus.ProvisioningAccepted)
        {
            if (!ProvisioningFactsAreCompatible(installation, accepted))
                throw new InvalidOperationException("installation already accepted different provisioning identities.");
            if (!AddsProvisioningFacts(installation, accepted))
                return;
            EnsureClaimedOutcome(
                installation,
                WorkflowInstallationStatus.ProvisioningAccepted,
                accepted.Attempt,
                accepted.OperationId,
                accepted.ContinuationClaimId,
                accepted.ContinuationClaimantId,
                actorNow);
            await PersistDomainEventAsync(accepted);
            return;
        }
        if (installation.Status == WorkflowInstallationStatus.Failed)
        {
            if (installation.FailureOriginStatus == WorkflowInstallationStatus.Accepted)
            {
                EnsureClaimedOutcome(
                    installation,
                    WorkflowInstallationStatus.Accepted,
                    accepted.Attempt,
                    accepted.OperationId,
                    accepted.ContinuationClaimId,
                    accepted.ContinuationClaimantId,
                    actorNow);
                await PersistDomainEventAsync(accepted);
            }
            return;
        }
        if (installation.Status != WorkflowInstallationStatus.Accepted)
            throw new InvalidOperationException("installation must be accepted before provisioning can be recorded.");
        EnsureClaimedOutcome(
            installation,
            WorkflowInstallationStatus.Accepted,
            accepted.Attempt,
            accepted.OperationId,
            accepted.ContinuationClaimId,
            accepted.ContinuationClaimantId,
            actorNow);
        await PersistDomainEventAsync(accepted);
    }

    [EventHandler(EndpointName = "recordWorkflowInstallationReady")]
    public async Task HandleInstallationReadyAsync(RecordWorkflowInstallationReadyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var actorNow = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var installation = RequireInstallation(command.InstallationId);
        if (!AcceptsOutcome(installation, command.Attempt, command.OperationId))
            return;
        if (installation.Status is not (WorkflowInstallationStatus.ProvisioningAccepted or
            WorkflowInstallationStatus.Ready))
            throw new InvalidOperationException("only a provisioning-accepted installation can become ready.");
        var evidence = NormalizeReadinessEvidence(command.Evidence);
        ValidateReadinessEvidence(installation, evidence);
        var readyAt = RequireTimestamp(command.ReadyAtUtc, "ready_at_utc");

        if (installation.Status == WorkflowInstallationStatus.Ready)
        {
            if (Equals(installation.ReadinessEvidence, evidence))
                return;
            throw new InvalidOperationException("installation is already ready with different readiness evidence.");
        }

        EnsureClaimedOutcome(
            installation,
            WorkflowInstallationStatus.ProvisioningAccepted,
            command.Attempt,
            command.OperationId,
            command.ContinuationClaimId,
            command.ContinuationClaimantId,
            actorNow);

        await PersistDomainEventAsync(new WorkflowInstallationReadyEvent
        {
            Evidence = evidence,
            ReadyAtUtc = readyAt.Clone(),
            Attempt = command.Attempt,
            OperationId = command.OperationId.Trim(),
            ContinuationClaimId = command.ContinuationClaimId.Trim(),
            ContinuationClaimantId = command.ContinuationClaimantId.Trim(),
        });
    }

    [EventHandler(EndpointName = "recordWorkflowInstallationFailed")]
    public async Task HandleInstallationFailedAsync(RecordWorkflowInstallationFailedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDelivery(command.DeliveryId);
        var actorNow = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var installation = RequireInstallation(command.InstallationId);
        if (!AcceptsOutcome(installation, command.Attempt, command.OperationId))
            return;
        var code = WorkflowDeliveryConventions.NormalizeRequired(command.ErrorCode, "error_code");
        var message = command.ErrorMessage?.Trim() ?? string.Empty;
        var expectedStatus = ValidateFailureExpectedStatus(command.ExpectedStatus);
        if (installation.Status == WorkflowInstallationStatus.Ready)
            return;
        if (installation.Status == WorkflowInstallationStatus.Failed)
        {
            if (string.Equals(installation.ErrorCode, code, StringComparison.Ordinal) &&
                string.Equals(installation.ErrorMessage, message, StringComparison.Ordinal) &&
                installation.FailureOriginStatus == expectedStatus)
                return;
            if (installation.FailureOriginStatus == WorkflowInstallationStatus.ProvisioningAccepted &&
                expectedStatus == WorkflowInstallationStatus.Accepted)
                return;
            throw new InvalidOperationException("installation already failed with a different outcome.");
        }
        if (installation.Status != expectedStatus)
        {
            if (installation.Status == WorkflowInstallationStatus.ProvisioningAccepted &&
                expectedStatus == WorkflowInstallationStatus.Accepted)
                return;
            throw new InvalidOperationException("installation failure expected status does not match the active stage.");
        }
        var failedAt = RequireTimestamp(command.FailedAtUtc, "failed_at_utc");
        EnsureClaimedOutcome(
            installation,
            expectedStatus,
            command.Attempt,
            command.OperationId,
            command.ContinuationClaimId,
            command.ContinuationClaimantId,
            actorNow);
        await PersistDomainEventAsync(new WorkflowInstallationFailedEvent
        {
            ErrorCode = code,
            ErrorMessage = message,
            FailedAtUtc = failedAt.Clone(),
            Attempt = command.Attempt,
            OperationId = command.OperationId.Trim(),
            FailedFromStatus = expectedStatus,
            ContinuationClaimId = command.ContinuationClaimId.Trim(),
            ContinuationClaimantId = command.ContinuationClaimantId.Trim(),
        });
    }

    protected override WorkflowDeliveryState TransitionState(WorkflowDeliveryState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowDeliveryCreatedEvent>(ApplyCreated)
            .On<WorkflowDeliveryAccessRecordedEvent>(ApplyAccessed)
            .On<WorkflowDeliveryRevokedEvent>(ApplyRevoked)
            .On<WorkflowDeliveryConnectionBegunEvent>(ApplyConnectionBegun)
            .On<WorkflowDeliveryConnectionUpdatedEvent>(ApplyConnectionUpdated)
            .On<WorkflowDeliveryConnectionAttachedEvent>(ApplyConnectionAttached)
            .On<WorkflowInstallationStartedEvent>(ApplyInstallationStarted)
            .On<WorkflowInstallationRetryRequestedEvent>(ApplyInstallationRetry)
            .On<WorkflowInstallationContinuationClaimedEvent>(ApplyInstallationContinuationClaimed)
            .On<WorkflowInstallationWithdrawnEvent>(ApplyInstallationWithdrawn)
            .On<WorkflowProvisioningAcceptedEvent>(ApplyProvisioningAccepted)
            .On<WorkflowInstallationReadyEvent>(ApplyInstallationReady)
            .On<WorkflowInstallationFailedEvent>(ApplyInstallationFailed)
            .OrCurrent();

    internal static WorkflowDeliveryState ApplyCreated(WorkflowDeliveryState _, WorkflowDeliveryCreatedEvent evt) =>
        new()
        {
            DeliveryId = evt.DeliveryId,
            Package = evt.Package.Clone(),
            TargetScopeId = evt.TargetScopeId,
            ExpiresAtUtc = evt.ExpiresAtUtc.Clone(),
            ExpiresAtDefaulted = evt.ExpiresAtDefaulted,
            Note = evt.Note,
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Active,
            CreatedBy = evt.CreatedBy,
            CreatedAtUtc = evt.CreatedAtUtc.Clone(),
        };

    internal static WorkflowDeliveryState ApplyAccessed(WorkflowDeliveryState state, WorkflowDeliveryAccessRecordedEvent evt)
    {
        var updated = state.Clone();
        updated.AccessedAtUtc = evt.AccessedAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyRevoked(WorkflowDeliveryState state, WorkflowDeliveryRevokedEvent evt)
    {
        var updated = state.Clone();
        updated.LifecycleStatus = WorkflowDeliveryLifecycleStatus.Revoked;
        updated.RevokedBy = evt.RevokedBy;
        updated.RevokedAtUtc = evt.RevokedAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyConnectionBegun(WorkflowDeliveryState state, WorkflowDeliveryConnectionBegunEvent evt)
    {
        var updated = state.Clone();
        var previous = updated.Connections.SingleOrDefault(x => x.SlotKey == evt.SlotKey);
        if (previous != null)
            updated.Connections.Remove(previous);
        updated.Connections.Add(new WorkflowDeliveryConnectionState
        {
            SlotKey = evt.SlotKey,
            ServiceSlug = evt.ServiceSlug,
            LinkId = evt.LinkId,
            Status = WorkflowDeliveryConnectionStatus.Pending,
            UpdatedAtUtc = evt.RequestedAtUtc.Clone(),
        });
        return updated;
    }

    internal static WorkflowDeliveryState ApplyConnectionUpdated(WorkflowDeliveryState state, WorkflowDeliveryConnectionUpdatedEvent evt)
    {
        var updated = state.Clone();
        var connection = updated.Connections.Single(x => x.SlotKey == evt.SlotKey && x.LinkId == evt.LinkId);
        connection.Status = evt.Status;
        connection.UserServiceId = evt.UserServiceId;
        connection.UpdatedAtUtc = evt.UpdatedAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyConnectionAttached(
        WorkflowDeliveryState state,
        WorkflowDeliveryConnectionAttachedEvent evt)
    {
        var updated = state.Clone();
        var previous = updated.Connections.SingleOrDefault(connection => connection.SlotKey == evt.SlotKey);
        if (previous != null)
            updated.Connections.Remove(previous);
        updated.Connections.Add(new WorkflowDeliveryConnectionState
        {
            SlotKey = evt.SlotKey,
            ServiceSlug = evt.ServiceSlug,
            Status = WorkflowDeliveryConnectionStatus.Completed,
            UserServiceId = evt.UserServiceId,
            UpdatedAtUtc = evt.AttachedAtUtc.Clone(),
        });
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationStarted(WorkflowDeliveryState state, WorkflowInstallationStartedEvent evt)
    {
        var updated = state.Clone();
        updated.Installation = evt.Installation.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationRetry(WorkflowDeliveryState state, WorkflowInstallationRetryRequestedEvent evt)
    {
        var updated = state.Clone();
        updated.Installation.Status = WorkflowInstallationStatus.Accepted;
        updated.Installation.Stage = "accepted";
        updated.Installation.ErrorCode = string.Empty;
        updated.Installation.ErrorMessage = string.Empty;
        updated.Installation.FailureOriginStatus = WorkflowInstallationStatus.Unspecified;
        updated.Installation.ScheduleProvisioningId = string.Empty;
        updated.Installation.ScheduleProvisioningStatus = string.Empty;
        updated.Installation.ReadinessEvidence = null;
        updated.Installation.Attempt++;
        updated.Installation.OperationId = evt.OperationId;
        updated.Installation.ContinuationClaim = null;
        updated.Installation.UpdatedAtUtc = evt.RequestedAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationContinuationClaimed(
        WorkflowDeliveryState state,
        WorkflowInstallationContinuationClaimedEvent evt)
    {
        var updated = state.Clone();
        updated.Installation.ContinuationClaim = evt.Claim.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationWithdrawn(
        WorkflowDeliveryState state,
        WorkflowInstallationWithdrawnEvent evt)
    {
        var updated = state.Clone();
        var installation = updated.Installation;
        installation.Status = WorkflowInstallationStatus.Failed;
        installation.Stage = "failed";
        (installation.ErrorCode, installation.ErrorMessage) = evt.Reason switch
        {
            WorkflowInstallationWithdrawalReason.DeliveryRevoked =>
                ("delivery_revoked", "The workflow delivery was revoked before installation completed."),
            WorkflowInstallationWithdrawalReason.DeliveryExpired =>
                ("delivery_expired", "The workflow delivery expired before installation completed."),
            _ => throw new InvalidOperationException("workflow installation withdrawal reason is required."),
        };
        installation.FailureOriginStatus = evt.FailedFromStatus;
        installation.ContinuationClaim = null;
        installation.UpdatedAtUtc = evt.WithdrawnAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyProvisioningAccepted(WorkflowDeliveryState state, WorkflowProvisioningAcceptedEvent evt)
    {
        var updated = state.Clone();
        var installation = updated.Installation;
        installation.Status = WorkflowInstallationStatus.ProvisioningAccepted;
        installation.Stage = "provisioning_accepted";
        installation.ErrorCode = string.Empty;
        installation.ErrorMessage = string.Empty;
        installation.FailureOriginStatus = WorkflowInstallationStatus.Unspecified;
        installation.MemberId = PreferExisting(installation.MemberId, evt.MemberId);
        installation.WorkflowId = PreferExisting(installation.WorkflowId, evt.WorkflowId);
        installation.PublishedServiceId = PreferExisting(installation.PublishedServiceId, evt.PublishedServiceId);
        installation.RevisionId = PreferExisting(installation.RevisionId, evt.RevisionId);
        installation.BindingRunId = PreferExisting(installation.BindingRunId, evt.BindingRunId);
        installation.ScheduleId = PreferExisting(installation.ScheduleId, evt.ScheduleId);
        installation.ScheduleProvisioningId = PreferExisting(
            installation.ScheduleProvisioningId,
            evt.ScheduleProvisioningId);
        installation.ScheduleProvisioningStatus = PreferExisting(
            installation.ScheduleProvisioningStatus,
            evt.ScheduleProvisioningStatus);
        installation.ReadinessEvidence = null;
        if (installation.UpdatedAtUtc == null || installation.UpdatedAtUtc.CompareTo(evt.AcceptedAtUtc) < 0)
            installation.UpdatedAtUtc = evt.AcceptedAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationReady(WorkflowDeliveryState state, WorkflowInstallationReadyEvent evt)
    {
        var updated = state.Clone();
        var installation = updated.Installation;
        installation.Status = WorkflowInstallationStatus.Ready;
        installation.Stage = "ready";
        installation.ErrorCode = string.Empty;
        installation.ErrorMessage = string.Empty;
        installation.FailureOriginStatus = WorkflowInstallationStatus.Unspecified;
        installation.ReadinessEvidence = evt.Evidence.Clone();
        installation.UpdatedAtUtc = evt.ReadyAtUtc.Clone();
        return updated;
    }

    internal static WorkflowDeliveryState ApplyInstallationFailed(WorkflowDeliveryState state, WorkflowInstallationFailedEvent evt)
    {
        var updated = state.Clone();
        updated.Installation.Status = WorkflowInstallationStatus.Failed;
        updated.Installation.Stage = "failed";
        updated.Installation.ErrorCode = evt.ErrorCode;
        updated.Installation.ErrorMessage = evt.ErrorMessage;
        updated.Installation.FailureOriginStatus = evt.FailedFromStatus;
        updated.Installation.UpdatedAtUtc = evt.FailedAtUtc.Clone();
        return updated;
    }

    private WorkflowInstallationWithdrawalReason ResolveWithdrawalReason(Timestamp at)
    {
        if (State.LifecycleStatus == WorkflowDeliveryLifecycleStatus.Revoked)
            return WorkflowInstallationWithdrawalReason.DeliveryRevoked;
        if (State.ExpiresAtUtc == null || at >= State.ExpiresAtUtc)
            return WorkflowInstallationWithdrawalReason.DeliveryExpired;
        return WorkflowInstallationWithdrawalReason.Unspecified;
    }

    private void EnsureCanonicalActor(string deliveryId)
    {
        var expected = WorkflowDeliveryConventions.BuildActorId(deliveryId);
        if (!string.Equals(Id, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"workflow delivery actor '{Id}' does not match '{expected}'.");
    }

    private void EnsureDelivery(string deliveryId)
    {
        if (State.DeliveryId.Length == 0)
            throw new InvalidOperationException("workflow delivery has not been created.");
        if (!string.Equals(State.DeliveryId, deliveryId?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("workflow delivery identity does not match.");
        EnsureCanonicalActor(State.DeliveryId);
    }

    private void EnsureTargetScope(string scopeId)
    {
        if (!string.Equals(State.TargetScopeId, scopeId?.Trim(), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("workflow delivery target scope does not match.");
    }

    private void EnsureAvailable(Timestamp? at)
    {
        var instant = RequireTimestamp(at, "requested_at_utc");
        if (State.LifecycleStatus != WorkflowDeliveryLifecycleStatus.Active)
            throw new InvalidOperationException("workflow delivery is not active.");
        if (State.ExpiresAtUtc == null || instant >= State.ExpiresAtUtc)
            throw new InvalidOperationException("workflow delivery has expired.");
    }

    private WorkflowDeliveryConnectionSlotDefinition FindSlot(string slotKey, string serviceSlug)
    {
        var key = WorkflowDeliveryConventions.NormalizeRequired(slotKey, "slot_key");
        var slug = WorkflowDeliveryConventions.NormalizeRequired(serviceSlug, "service_slug");
        return State.Package.ConnectionSlots.SingleOrDefault(slot =>
                   string.Equals(slot.Key, key, StringComparison.Ordinal) &&
                   string.Equals(slot.ServiceSlug, slug, StringComparison.Ordinal))
               ?? throw new InvalidOperationException("connection slot is not part of the package contract.");
    }

    private WorkflowInstallationState RequireInstallation(string installationId)
    {
        var normalized = WorkflowDeliveryConventions.NormalizeRequired(installationId, "installation_id");
        if (State.Installation == null ||
            !string.Equals(State.Installation.InstallationId, normalized, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow installation was not found.");
        return State.Installation;
    }

    private static bool AcceptsOutcome(
        WorkflowInstallationState installation,
        int attempt,
        string operationId)
    {
        if (attempt <= 0)
            throw new InvalidOperationException("provisioning outcome attempt must be positive.");
        var normalizedOperationId = WorkflowDeliveryConventions.NormalizeRequired(operationId, "operation_id");
        if (attempt < installation.Attempt)
            return false;
        if (attempt > installation.Attempt)
            throw new InvalidOperationException("provisioning outcome attempt is ahead of the active installation attempt.");
        if (!string.Equals(installation.OperationId, normalizedOperationId, StringComparison.Ordinal))
            throw new InvalidOperationException("provisioning outcome operation identity does not match the active attempt.");
        return true;
    }

    private void EnsureClaimedOutcome(
        WorkflowInstallationState installation,
        WorkflowInstallationStatus expectedStatus,
        int attempt,
        string operationId,
        string claimId,
        string claimantId,
        Timestamp actorNow)
    {
        var normalizedClaimId = WorkflowDeliveryConventions.NormalizeRequired(
            claimId,
            "continuation_claim_id");
        var normalizedClaimantId = WorkflowDeliveryConventions.NormalizeRequired(
            claimantId,
            "continuation_claimant_id");
        var claim = installation.ContinuationClaim
            ?? throw new InvalidOperationException("installation continuation has not been claimed.");
        if (!string.Equals(claim.ClaimId, normalizedClaimId, StringComparison.Ordinal) ||
            !string.Equals(claim.ClaimantId, normalizedClaimantId, StringComparison.Ordinal) ||
            claim.ExpectedStatus != expectedStatus ||
            claim.Attempt != attempt ||
            !string.Equals(claim.OperationId, operationId?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "installation continuation claim does not own the active operation stage.");
        }
        if (claim.ClaimedAtUtc == null || claim.ExpiresAtUtc == null ||
            actorNow >= claim.ExpiresAtUtc)
        {
            throw new InvalidOperationException("installation continuation claim is not active.");
        }
    }

    private WorkflowInstallationContinuationClaim NormalizeContinuationClaim(
        ClaimWorkflowInstallationContinuationCommand command,
        Timestamp actorNow)
    {
        if (command.ExpectedStatus is not (WorkflowInstallationStatus.Accepted or
            WorkflowInstallationStatus.ProvisioningAccepted))
        {
            throw new InvalidOperationException(
                "continuation claim expected status must identify an active installation stage.");
        }
        if (command.Attempt <= 0)
            throw new InvalidOperationException("continuation claim attempt must be positive.");
        if (command.RequestedDuration == null)
            throw new InvalidOperationException("continuation claim duration is required.");
        var duration = command.RequestedDuration.ToTimeSpan();
        if (duration <= TimeSpan.Zero || duration > MaximumContinuationClaimDuration)
        {
            throw new InvalidOperationException(
                "continuation claim duration must be positive and within the maximum duration.");
        }
        var expiresAt = Timestamp.FromDateTimeOffset(actorNow.ToDateTimeOffset().Add(duration));
        if (State.ExpiresAtUtc != null && expiresAt > State.ExpiresAtUtc)
            expiresAt = State.ExpiresAtUtc.Clone();
        return new WorkflowInstallationContinuationClaim
        {
            ClaimId = WorkflowDeliveryConventions.NormalizeRequired(command.ClaimId, "claim_id"),
            ClaimantId = WorkflowDeliveryConventions.NormalizeRequired(command.ClaimantId, "claimant_id"),
            ExpectedStatus = command.ExpectedStatus,
            Attempt = command.Attempt,
            OperationId = WorkflowDeliveryConventions.NormalizeRequired(command.OperationId, "operation_id"),
            ClaimedAtUtc = actorNow.Clone(),
            ExpiresAtUtc = expiresAt,
        };
    }

    private static bool SameContinuationClaimIdentity(
        WorkflowInstallationContinuationClaim existing,
        WorkflowInstallationContinuationClaim candidate) =>
        string.Equals(existing.ClaimId, candidate.ClaimId, StringComparison.Ordinal) &&
        string.Equals(existing.ClaimantId, candidate.ClaimantId, StringComparison.Ordinal) &&
        existing.ExpectedStatus == candidate.ExpectedStatus &&
        existing.Attempt == candidate.Attempt &&
        string.Equals(existing.OperationId, candidate.OperationId, StringComparison.Ordinal);

    private static bool ContinuationClaimIsActiveFor(
        WorkflowInstallationContinuationClaim claim,
        WorkflowInstallationState installation,
        Timestamp at) =>
        claim.ExpectedStatus == installation.Status &&
        claim.Attempt == installation.Attempt &&
        string.Equals(claim.OperationId, installation.OperationId, StringComparison.Ordinal) &&
        claim.ClaimedAtUtc != null &&
        claim.ExpiresAtUtc != null &&
        at < claim.ExpiresAtUtc;

    private static WorkflowInstallationStatus ValidateFailureExpectedStatus(
        WorkflowInstallationStatus expectedStatus)
    {
        if (expectedStatus is not (WorkflowInstallationStatus.Accepted or
            WorkflowInstallationStatus.ProvisioningAccepted))
        {
            throw new InvalidOperationException(
                "installation failure expected status must identify an active installation stage.");
        }

        return expectedStatus;
    }

    private static void ValidateCreate(CreateWorkflowDeliveryCommand command)
    {
        WorkflowDeliveryConventions.NormalizeRequired(command.DeliveryId, "delivery_id");
        WorkflowDeliveryConventions.NormalizeRequired(command.TargetScopeId, "target_scope_id");
        WorkflowDeliveryConventions.NormalizeRequired(command.CreatedBy, "created_by");
        RequireTimestamp(command.CreatedAtUtc, "created_at_utc");
        var expiresAt = RequireTimestamp(command.ExpiresAtUtc, "expires_at_utc");
        if (expiresAt <= command.CreatedAtUtc)
            throw new InvalidOperationException("workflow delivery expires_at_utc must be later than created_at_utc.");
        ValidatePackage(command.Package);
    }

    private static void ValidatePackage(WorkflowPackageVersionSnapshot? package)
    {
        ArgumentNullException.ThrowIfNull(package);
        WorkflowDeliveryConventions.NormalizeRequired(package.PackageId, "package_id");
        WorkflowDeliveryConventions.NormalizeRequired(package.PackageVersionId, "package_version_id");
        WorkflowDeliveryConventions.NormalizeRequired(package.WorkflowName, "workflow_name");
        WorkflowDeliveryConventions.NormalizeRequired(package.Version, "version");
        WorkflowDeliveryConventions.NormalizeRequiredDocument(package.SourceYaml, "source_yaml");
        WorkflowDeliveryConventions.NormalizeRequired(package.SourceHash, "source_hash");
        WorkflowDeliveryConventions.NormalizeRequired(package.PackageHash, "package_hash");
        WorkflowDeliveryConventions.NormalizeRequired(package.CreatedBy, "package.created_by");
        RequireTimestamp(package.CreatedAtUtc, "package.created_at_utc");
        if (package.AcceptancePolicy == null ||
            package.AcceptancePolicy.Mode is not (
                WorkflowDeliveryAcceptanceMode.AutomaticPreview or
                WorkflowDeliveryAcceptanceMode.Manual))
            throw new InvalidOperationException("workflow delivery acceptance policy is required.");
        if (package.AcceptancePolicy.Mode == WorkflowDeliveryAcceptanceMode.Manual &&
            string.IsNullOrWhiteSpace(package.AcceptancePolicy.Limitation))
            throw new InvalidOperationException("manual workflow delivery acceptance policy requires a limitation.");
        WorkflowDeliveryConventions.ValidateAcceptanceInput(package.AcceptancePolicy.Input);
        var expectedPackageHash = WorkflowDeliveryConventions.ComputePackageHash(package);
        if (!string.Equals(package.PackageHash, expectedPackageHash, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow delivery package hash does not match its immutable content.");
        if (!string.Equals(package.Version, expectedPackageHash[..16], StringComparison.Ordinal) ||
            !string.Equals(
                package.PackageVersionId,
                WorkflowDeliveryConventions.BuildPackageVersionId(package.WorkflowName, expectedPackageHash),
                StringComparison.Ordinal))
            throw new InvalidOperationException("workflow delivery package version identity does not match its immutable content.");
        if (package.VariableSchema.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != package.VariableSchema.Count)
            throw new InvalidOperationException("workflow delivery variable keys must be unique.");
        if (package.ConnectionSlots.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != package.ConnectionSlots.Count)
            throw new InvalidOperationException("workflow delivery connection slot keys must be unique.");
    }

    private void ValidateInstallation(StartWorkflowInstallationCommand command)
    {
        WorkflowDeliveryConventions.NormalizeRequired(command.InstallationId, "installation_id");
        WorkflowDeliveryConventions.NormalizeRequired(command.IdempotencyKey, "idempotency_key");
        WorkflowDeliveryConventions.NormalizeRequired(command.TeamId, "team_id");
        WorkflowDeliveryConventions.NormalizeRequiredDocument(command.ResolvedYaml, "resolved_yaml");
        WorkflowDeliveryConventions.NormalizeRequired(command.ResolvedHash, "resolved_hash");
        WorkflowDeliveryConventions.NormalizeRequired(command.OperationId, "operation_id");
        ArgumentNullException.ThrowIfNull(command.CapabilityAdmissionPlan);
        if (!string.Equals(command.SourceHash?.Trim(), State.Package.SourceHash, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow installation source hash does not match the immutable package.");
        if (command.TriggerIntent == null || command.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.Unspecified)
            throw new InvalidOperationException("workflow installation trigger intent is required.");
        var recipeRequiresOwner = State.Package.AcceptancePolicy?.Input?.Bindings.Any(static binding =>
            binding.SourceCase ==
                WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId) == true;
        if (command.AuthenticatedOwner != null || recipeRequiresOwner ||
            command.TriggerIntent.Kind is WorkflowDeliveryTriggerKind.OneShot or WorkflowDeliveryTriggerKind.Cron)
            ValidateAuthorizationOwner(command.AuthenticatedOwner);
        var schemaKeys = State.Package.VariableSchema.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (command.ConfigurationValues.Keys.Any(key => !schemaKeys.Contains(key)))
            throw new InvalidOperationException("workflow installation contains a configuration key outside the package schema.");
        var slotKeys = State.Package.ConnectionSlots.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (command.ConnectionReferences.Keys.Any(key => !slotKeys.Contains(key)))
            throw new InvalidOperationException("workflow installation contains a connection outside the package schema.");
        foreach (var required in State.Package.ConnectionSlots.Where(x => x.Required))
        {
            if (!command.ConnectionReferences.TryGetValue(required.Key, out var reference) || string.IsNullOrWhiteSpace(reference))
                throw new InvalidOperationException($"required connection slot '{required.Key}' is unresolved.");
        }
        var completedReferences = State.Connections
            .Where(static connection =>
                connection.Status == WorkflowDeliveryConnectionStatus.Completed &&
                !string.IsNullOrWhiteSpace(connection.UserServiceId))
            .ToDictionary(
                static connection => connection.SlotKey,
                static connection => connection.UserServiceId,
                StringComparer.Ordinal);
        if (completedReferences.Count != command.ConnectionReferences.Count ||
            command.ConnectionReferences.Any(reference =>
                !completedReferences.TryGetValue(reference.Key, out var completedReference) ||
                !string.Equals(completedReference, reference.Value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "workflow installation connection references do not match completed delivery connections.");
        }
        foreach (var confirmation in command.Confirmations)
        {
            WorkflowDeliveryConventions.NormalizeRequired(confirmation.CallSiteId, "confirmation.call_site_id");
            WorkflowDeliveryConventions.NormalizeRequired(
                confirmation.RequestContractDigest,
                "confirmation.request_contract_digest");
        }
        if (command.Confirmations.Select(static value => value.CallSiteId.Trim())
            .Distinct(StringComparer.Ordinal).Count() != command.Confirmations.Count)
            throw new InvalidOperationException("workflow installation confirmation call-site identities must be unique.");
    }

    private void EnsureConnectionsMutable()
    {
        if (State.Installation != null && State.Installation.InstallationId.Length != 0)
        {
            throw new InvalidOperationException(
                "workflow delivery connections cannot be changed after installation has started.");
        }
    }

    private static void ValidateAuthorizationOwner(WorkflowDeliveryAuthorizationOwnerContext? context)
    {
        if (context == null)
            throw new InvalidOperationException("workflow installation requires authenticated authorization owner context.");
        ValidateOwnerIdentity(context.Owner, "authenticated_owner.owner");
        var subjectPlatform = WorkflowDeliveryConventions.NormalizeRequired(
            context.SubjectPlatform,
            "authenticated_owner.subject_platform");
        if (!string.Equals(subjectPlatform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(context.SubjectTenant))
        {
            WorkflowDeliveryConventions.NormalizeRequired(
                context.SubjectTenant,
                "authenticated_owner.subject_tenant");
        }
        WorkflowDeliveryConventions.NormalizeRequired(
            context.SubjectExternalUserId,
            "authenticated_owner.subject_external_user_id");
        WorkflowDeliveryConventions.NormalizeRequired(
            context.VerifiedBindingId,
            "authenticated_owner.verified_binding_id");
        if (context.AuthenticatedActor != null)
            ValidateOwnerIdentity(context.AuthenticatedActor, "authenticated_owner.authenticated_actor");
    }

    private static void ValidateOwnerIdentity(AuthorizationOwnerIdentity? identity, string name)
    {
        if (identity == null || identity.OwnerKind == AuthorizationOwnerKind.Unspecified)
            throw new InvalidOperationException($"{name} is required.");
        WorkflowDeliveryConventions.NormalizeRequired(identity.Authority, $"{name}.authority");
        WorkflowDeliveryConventions.NormalizeRequired(identity.OwnerSubject, $"{name}.owner_subject");
    }

    private static WorkflowProvisioningAcceptedEvent NormalizeProvisioningAccepted(
        RecordWorkflowProvisioningAcceptedCommand command) =>
        new()
        {
            MemberId = WorkflowDeliveryConventions.NormalizeRequired(command.MemberId, "member_id"),
            WorkflowId = command.WorkflowId?.Trim() ?? string.Empty,
            PublishedServiceId = command.PublishedServiceId?.Trim() ?? string.Empty,
            RevisionId = command.RevisionId?.Trim() ?? string.Empty,
            BindingRunId = command.BindingRunId?.Trim() ?? string.Empty,
            ScheduleId = command.ScheduleId?.Trim() ?? string.Empty,
            ScheduleProvisioningId = command.ScheduleProvisioningId?.Trim() ?? string.Empty,
            ScheduleProvisioningStatus = command.ScheduleProvisioningStatus?.Trim() ?? string.Empty,
            AcceptedAtUtc = RequireTimestamp(command.AcceptedAtUtc, "accepted_at_utc").Clone(),
            Attempt = command.Attempt,
            OperationId = command.OperationId.Trim(),
            ContinuationClaimId = command.ContinuationClaimId?.Trim() ?? string.Empty,
            ContinuationClaimantId = command.ContinuationClaimantId?.Trim() ?? string.Empty,
        };

    private static bool SameProvisioning(
        WorkflowInstallationState installation,
        WorkflowProvisioningAcceptedEvent accepted) =>
        string.Equals(installation.MemberId, accepted.MemberId, StringComparison.Ordinal) &&
        string.Equals(installation.WorkflowId, accepted.WorkflowId, StringComparison.Ordinal) &&
        string.Equals(installation.PublishedServiceId, accepted.PublishedServiceId, StringComparison.Ordinal) &&
        string.Equals(installation.RevisionId, accepted.RevisionId, StringComparison.Ordinal) &&
        string.Equals(installation.BindingRunId, accepted.BindingRunId, StringComparison.Ordinal) &&
        string.Equals(installation.ScheduleId, accepted.ScheduleId, StringComparison.Ordinal) &&
        string.Equals(installation.ScheduleProvisioningId, accepted.ScheduleProvisioningId, StringComparison.Ordinal) &&
        string.Equals(installation.ScheduleProvisioningStatus, accepted.ScheduleProvisioningStatus, StringComparison.Ordinal);

    private static bool ProvisioningFactsAreCompatible(
        WorkflowInstallationState installation,
        WorkflowProvisioningAcceptedEvent accepted) =>
        string.Equals(installation.MemberId, accepted.MemberId, StringComparison.Ordinal) &&
        string.Equals(installation.WorkflowId, accepted.WorkflowId, StringComparison.Ordinal) &&
        string.Equals(installation.PublishedServiceId, accepted.PublishedServiceId, StringComparison.Ordinal) &&
        string.Equals(installation.RevisionId, accepted.RevisionId, StringComparison.Ordinal) &&
        OptionalFactIsCompatible(installation.BindingRunId, accepted.BindingRunId) &&
        OptionalFactIsCompatible(installation.ScheduleId, accepted.ScheduleId) &&
        OptionalFactIsCompatible(installation.ScheduleProvisioningId, accepted.ScheduleProvisioningId) &&
        OptionalFactIsCompatible(
            installation.ScheduleProvisioningStatus,
            accepted.ScheduleProvisioningStatus);

    private static bool AddsProvisioningFacts(
        WorkflowInstallationState installation,
        WorkflowProvisioningAcceptedEvent accepted) =>
        AddsOptionalFact(installation.BindingRunId, accepted.BindingRunId) ||
        AddsOptionalFact(installation.ScheduleId, accepted.ScheduleId) ||
        AddsOptionalFact(installation.ScheduleProvisioningId, accepted.ScheduleProvisioningId) ||
        AddsOptionalFact(installation.ScheduleProvisioningStatus, accepted.ScheduleProvisioningStatus);

    private static bool OptionalFactIsCompatible(string existing, string candidate) =>
        existing.Length == 0 || candidate.Length == 0 || string.Equals(existing, candidate, StringComparison.Ordinal);

    private static bool AddsOptionalFact(string existing, string candidate) =>
        existing.Length == 0 && candidate.Length != 0;

    private static string PreferExisting(string existing, string candidate) =>
        existing.Length == 0 ? candidate : existing;

    private static WorkflowInstallationReadinessEvidence NormalizeReadinessEvidence(
        WorkflowInstallationReadinessEvidence? source)
    {
        if (source == null)
            throw new InvalidOperationException("readiness evidence is required.");

        var normalized = new WorkflowInstallationReadinessEvidence();
        if (source.PublishedService != null)
        {
            normalized.PublishedService = new WorkflowPublishedServiceReadinessEvidence
            {
                PublishedServiceId = source.PublishedService.PublishedServiceId?.Trim() ?? string.Empty,
                Committed = source.PublishedService.Committed,
                Runnable = source.PublishedService.Runnable,
                CommittedStateVersion = source.PublishedService.CommittedStateVersion,
            };
        }
        if (source.BoundRevision != null)
        {
            normalized.BoundRevision = new WorkflowBoundRevisionReadinessEvidence
            {
                RevisionId = source.BoundRevision.RevisionId?.Trim() ?? string.Empty,
                BindingRunId = source.BoundRevision.BindingRunId?.Trim() ?? string.Empty,
                Bound = source.BoundRevision.Bound,
                CommittedStateVersion = source.BoundRevision.CommittedStateVersion,
            };
        }
        if (source.Trigger != null)
            normalized.Trigger = NormalizeTriggerReadinessEvidence(source.Trigger);
        if (source.AcceptanceRun != null)
        {
            normalized.AcceptanceRun = new WorkflowAcceptanceRunReadinessEvidence
            {
                AcceptanceRunId = source.AcceptanceRun.AcceptanceRunId?.Trim() ?? string.Empty,
                Status = source.AcceptanceRun.Status,
                CommittedStateVersion = source.AcceptanceRun.CommittedStateVersion,
            };
        }
        normalized.Artifacts.Add(source.Artifacts.Select(static artifact =>
            new WorkflowInstallationArtifactEvidence
            {
                Kind = artifact.Kind,
                ArtifactId = artifact.ArtifactId?.Trim() ?? string.Empty,
                VerificationStatus = artifact.VerificationStatus,
                VerificationReference = artifact.VerificationReference?.Trim() ?? string.Empty,
                ContentDigest = artifact.ContentDigest?.Trim() ?? string.Empty,
            }));
        return normalized;
    }

    private static WorkflowTriggerReadinessEvidence NormalizeTriggerReadinessEvidence(
        WorkflowTriggerReadinessEvidence source)
    {
        var normalized = new WorkflowTriggerReadinessEvidence();
        if (source.Intent != null)
        {
            normalized.Intent = new WorkflowDeliveryTriggerIntent
            {
                Kind = source.Intent.Kind,
                Cron = source.Intent.Cron?.Trim() ?? string.Empty,
                TimeZone = source.Intent.TimeZone?.Trim() ?? string.Empty,
                RunImmediately = source.Intent.RunImmediately,
            };
        }
        switch (source.ReadinessCase)
        {
            case WorkflowTriggerReadinessEvidence.ReadinessOneofCase.NoTrigger:
                normalized.NoTrigger = source.NoTrigger.Clone();
                break;
            case WorkflowTriggerReadinessEvidence.ReadinessOneofCase.Schedule:
                normalized.Schedule = new WorkflowScheduleReadinessEvidence
                {
                    ScheduleId = source.Schedule.ScheduleId?.Trim() ?? string.Empty,
                    ScheduleProvisioningId = source.Schedule.ScheduleProvisioningId?.Trim() ?? string.Empty,
                    Status = source.Schedule.Status,
                    CommittedStateVersion = source.Schedule.CommittedStateVersion,
                };
                break;
        }
        return normalized;
    }

    private static void ValidateReadinessEvidence(
        WorkflowInstallationState installation,
        WorkflowInstallationReadinessEvidence evidence)
    {
        ValidatePublishedServiceEvidence(installation, evidence.PublishedService);
        ValidateBoundRevisionEvidence(installation, evidence.BoundRevision);
        ValidateTriggerEvidence(installation, evidence.Trigger);

        if (installation.TriggerIntent?.Kind == WorkflowDeliveryTriggerKind.None)
        {
            if (evidence.AcceptanceRun != null || evidence.Artifacts.Count != 0)
            {
                throw new InvalidOperationException(
                    "a no-trigger installation must not carry acceptance-run or artifact evidence.");
            }
            return;
        }

        ValidateAcceptanceRunEvidence(evidence.AcceptanceRun);
        ValidateArtifactEvidence(evidence.Artifacts);
    }

    private static void ValidatePublishedServiceEvidence(
        WorkflowInstallationState installation,
        WorkflowPublishedServiceReadinessEvidence? evidence)
    {
        if (evidence == null)
            throw new InvalidOperationException("published-service readiness evidence is required.");
        var publishedServiceId = WorkflowDeliveryConventions.NormalizeRequired(
            installation.PublishedServiceId,
            "installation.published_service_id");
        if (!string.Equals(evidence.PublishedServiceId, publishedServiceId, StringComparison.Ordinal))
            throw new InvalidOperationException("published-service readiness evidence does not match the installation.");
        if (!evidence.Committed || evidence.CommittedStateVersion <= 0)
            throw new InvalidOperationException("published-service readiness evidence must prove a committed state.");
        if (!evidence.Runnable)
            throw new InvalidOperationException("published-service readiness evidence must prove the service is runnable.");
    }

    private static void ValidateBoundRevisionEvidence(
        WorkflowInstallationState installation,
        WorkflowBoundRevisionReadinessEvidence? evidence)
    {
        if (evidence == null)
            throw new InvalidOperationException("bound-revision readiness evidence is required.");
        var revisionId = WorkflowDeliveryConventions.NormalizeRequired(
            installation.RevisionId,
            "installation.revision_id");
        if (!string.Equals(evidence.RevisionId, revisionId, StringComparison.Ordinal))
            throw new InvalidOperationException("bound-revision readiness evidence does not match the installation.");
        if (installation.BindingRunId.Length == 0)
        {
            if (evidence.BindingRunId.Length != 0)
                throw new InvalidOperationException("bound-revision readiness evidence names an unknown binding run.");
        }
        else if (!string.Equals(evidence.BindingRunId, installation.BindingRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("bound-revision readiness evidence does not match the installation.");
        }
        if (!evidence.Bound || evidence.CommittedStateVersion <= 0)
            throw new InvalidOperationException("bound-revision readiness evidence must prove a committed binding.");
    }

    private static void ValidateTriggerEvidence(
        WorkflowInstallationState installation,
        WorkflowTriggerReadinessEvidence? evidence)
    {
        if (evidence?.Intent == null || installation.TriggerIntent == null ||
            !SameTriggerIntent(installation.TriggerIntent, evidence.Intent))
            throw new InvalidOperationException("trigger readiness evidence does not match the installation trigger intent.");

        if (installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.None)
        {
            if (evidence.ReadinessCase != WorkflowTriggerReadinessEvidence.ReadinessOneofCase.NoTrigger ||
                evidence.NoTrigger == null ||
                !evidence.NoTrigger.Ready)
                throw new InvalidOperationException("a no-trigger installation requires explicit no-trigger-ready evidence.");
            return;
        }

        if (installation.TriggerIntent.Kind is not (WorkflowDeliveryTriggerKind.OneShot or
            WorkflowDeliveryTriggerKind.Cron))
            throw new InvalidOperationException("installation trigger intent is not supported for readiness.");
        if (evidence.ReadinessCase != WorkflowTriggerReadinessEvidence.ReadinessOneofCase.Schedule ||
            evidence.Schedule == null)
            throw new InvalidOperationException("a scheduled installation requires schedule readiness evidence.");

        var scheduleId = WorkflowDeliveryConventions.NormalizeRequired(
            installation.ScheduleId,
            "installation.schedule_id");
        if (!string.Equals(evidence.Schedule.ScheduleId, scheduleId, StringComparison.Ordinal))
            throw new InvalidOperationException("schedule readiness evidence does not match the installation schedule.");
        if (installation.ScheduleProvisioningId.Length != 0 &&
            !string.Equals(
                evidence.Schedule.ScheduleProvisioningId,
                installation.ScheduleProvisioningId,
                StringComparison.Ordinal))
            throw new InvalidOperationException("schedule readiness evidence does not match schedule provisioning.");
        if (evidence.Schedule.Status != WorkflowScheduleReadinessStatus.Ready ||
            evidence.Schedule.CommittedStateVersion <= 0)
            throw new InvalidOperationException("schedule readiness evidence must prove a committed ready schedule.");
    }

    private static void ValidateAcceptanceRunEvidence(WorkflowAcceptanceRunReadinessEvidence? evidence)
    {
        if (evidence == null)
            throw new InvalidOperationException("acceptance-run readiness evidence is required.");
        WorkflowDeliveryConventions.NormalizeRequired(evidence.AcceptanceRunId, "acceptance_run_id");
        if (evidence.Status != WorkflowAcceptanceRunStatus.TerminalSuccess ||
            evidence.CommittedStateVersion <= 0)
            throw new InvalidOperationException("acceptance-run readiness evidence must prove committed terminal success.");
    }

    private static void ValidateArtifactEvidence(
        IEnumerable<WorkflowInstallationArtifactEvidence> artifacts)
    {
        var items = artifacts.ToArray();
        if (items.Length == 0)
            throw new InvalidOperationException("at least one typed artifact evidence item is required.");

        var identities = new HashSet<(WorkflowInstallationArtifactKind Kind, string ArtifactId)>();
        foreach (var artifact in items)
        {
            if (artifact.Kind == WorkflowInstallationArtifactKind.Unspecified)
                throw new InvalidOperationException("artifact evidence kind is required.");
            var artifactId = WorkflowDeliveryConventions.NormalizeRequired(artifact.ArtifactId, "artifact_id");
            WorkflowDeliveryConventions.NormalizeRequired(
                artifact.VerificationReference,
                "artifact.verification_reference");
            WorkflowDeliveryConventions.NormalizeRequired(artifact.ContentDigest, "artifact.content_digest");
            if (artifact.VerificationStatus != WorkflowInstallationArtifactVerificationStatus.Verified)
                throw new InvalidOperationException("artifact evidence must be verified.");
            if (!identities.Add((artifact.Kind, artifactId)))
                throw new InvalidOperationException("artifact readiness evidence identities must be unique.");
        }
    }

    private static bool SameTriggerIntent(
        WorkflowDeliveryTriggerIntent expected,
        WorkflowDeliveryTriggerIntent actual) =>
        expected.Kind == actual.Kind &&
        string.Equals(expected.Cron?.Trim(), actual.Cron?.Trim(), StringComparison.Ordinal) &&
        string.Equals(expected.TimeZone?.Trim(), actual.TimeZone?.Trim(), StringComparison.Ordinal) &&
        expected.RunImmediately == actual.RunImmediately;

    private static bool SameCreate(WorkflowDeliveryState state, CreateWorkflowDeliveryCommand command) =>
        string.Equals(state.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.TargetScopeId, command.TargetScopeId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.Package?.PackageVersionId, command.Package?.PackageVersionId, StringComparison.Ordinal) &&
        string.Equals(state.Package?.SourceHash, command.Package?.SourceHash, StringComparison.Ordinal) &&
        state.ExpiresAtDefaulted == command.ExpiresAtDefaulted &&
        (state.ExpiresAtDefaulted || Equals(state.ExpiresAtUtc, command.ExpiresAtUtc)) &&
        string.Equals(state.Note, command.Note?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(state.CreatedBy, command.CreatedBy?.Trim(), StringComparison.Ordinal);

    private static bool SameInstallation(
        WorkflowInstallationState state,
        StartWorkflowInstallationCommand command) =>
        string.Equals(state.InstallationId, command.InstallationId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.IdempotencyKey, command.IdempotencyKey?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ScopeId, command.ScopeId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.TeamId, command.TeamId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.OperationId, command.OperationId?.Trim(), StringComparison.Ordinal) &&
        Equals(state.CapabilityAdmissionPlan, command.CapabilityAdmissionPlan) &&
        Equals(state.AuthenticatedOwner, command.AuthenticatedOwner) &&
        state.TriggerIntent != null && command.TriggerIntent != null &&
        SameTriggerIntent(state.TriggerIntent, command.TriggerIntent) &&
        string.Equals(state.SourceHash, command.SourceHash?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ResolvedHash, command.ResolvedHash?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ResolvedYaml, command.ResolvedYaml, StringComparison.Ordinal) &&
        state.ConfigurationValues.OrderBy(x => x.Key).SequenceEqual(command.ConfigurationValues.OrderBy(x => x.Key)) &&
        state.ConnectionReferences.OrderBy(x => x.Key).SequenceEqual(command.ConnectionReferences.OrderBy(x => x.Key)) &&
        SameConfirmations(state.Confirmations, command.Confirmations);

    private static bool SameConfirmations(
        IEnumerable<WorkflowDeliveryConfirmationReference> expected,
        IEnumerable<WorkflowDeliveryConfirmationReference> actual) =>
        expected.Select(NormalizeConfirmation)
            .OrderBy(static value => value.CallSiteId, StringComparer.Ordinal)
            .ThenBy(static value => value.RequestContractDigest, StringComparer.Ordinal)
            .SequenceEqual(actual.Select(NormalizeConfirmation)
                .OrderBy(static value => value.CallSiteId, StringComparer.Ordinal)
                .ThenBy(static value => value.RequestContractDigest, StringComparer.Ordinal));

    private static (string CallSiteId, string RequestContractDigest) NormalizeConfirmation(
        WorkflowDeliveryConfirmationReference value) =>
        (value.CallSiteId?.Trim() ?? string.Empty, value.RequestContractDigest?.Trim() ?? string.Empty);

    private static Timestamp RequireTimestamp(Timestamp? value, string name) =>
        value ?? throw new InvalidOperationException($"{name} is required.");
}
