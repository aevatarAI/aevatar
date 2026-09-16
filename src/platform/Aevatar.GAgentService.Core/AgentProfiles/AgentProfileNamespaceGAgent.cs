using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.AgentProfiles;

[GAgent("gagent.agent-profile-namespace")]
public sealed class AgentProfileNamespaceGAgent : GAgentBase<AgentProfileNamespaceState>, IProjectedActor
{
    public const string DurableProjectionKind = "agent-profile-catalog";
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(30);

    public static string ProjectionKind => DurableProjectionKind;

    [EventHandler]
    public async Task HandleCreateAsync(CreateAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureActorAddress(command.Owner);
        var operation = CanonicalOperation(command.Operation, command);
        if (AgentProfilePolicies.ValidateProfileSlug(command.ProfileSlug).Count > 0 ||
            string.IsNullOrWhiteSpace(command.ProfileId))
        {
            throw new InvalidOperationException("A valid Agent Profile target is required.");
        }
        EnsureOwner(command.Owner);

        if (FindOperation(operation.OperationId) is { } existing)
        {
            EnsureOperationReplay(existing, operation);
            var existingEntry = State.Profiles.FirstOrDefault(x =>
                string.Equals(x.ProvisioningOperationId, existing.OperationId, StringComparison.Ordinal));
            if (existingEntry is null)
                throw new InvalidOperationException("Agent Profile replay target is missing from authority state.");
            if (existingEntry.Status == AgentProfileProvisioningStatus.Failed)
            {
                await RetryProvisioningAsync(existingEntry, existing);
                return;
            }
            if (existingEntry.Status == AgentProfileProvisioningStatus.Provisioning)
            {
                await ScheduleProvisioningTimeoutAsync(existingEntry);
                await SendInitializeAsync(existingEntry, existing);
            }
            return;
        }

        if (State.Profiles.Any(x =>
                string.Equals(x.ProfileSlug, command.ProfileSlug, StringComparison.Ordinal) &&
                x.Status is AgentProfileProvisioningStatus.Provisioning or AgentProfileProvisioningStatus.Active))
        {
            await PersistRejectedAsync(operation, command.Owner, "PROFILE_SLUG_TAKEN");
            return;
        }
        if (State.Profiles.Any(x => string.Equals(x.ProfileId, command.ProfileId, StringComparison.Ordinal)))
        {
            await PersistRejectedAsync(operation, command.Owner, "PROFILE_ID_TAKEN");
            return;
        }

        var next = State.Clone();
        next.Owner = command.Owner.Clone();
        var entry = new AgentProfileCatalogEntry
        {
            ProfileId = command.ProfileId.Trim(),
            ProfileSlug = command.ProfileSlug,
            ProfileActorId = AgentProfileActorIds.Profile(command.ProfileId),
            Status = AgentProfileProvisioningStatus.Provisioning,
            ProvisioningOperationId = operation.OperationId,
            ProvisioningInputSha256 = operation.InputSha256,
            ProvisioningAttempt = 1,
            ProvisioningTimeoutCallbackId = BuildProvisioningTimeoutCallbackId(command.ProfileId, 1),
        };
        next.Profiles.Add(entry);
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(operation, AgentProfileMutationStatus.Succeeded,
            "PROFILE_PROVISIONING_STARTED", NextVersion());
        await PersistAsync(next, "provisioning-started");
        await ScheduleProvisioningTimeoutAsync(entry);
        await SendInitializeAsync(entry, operation);
    }

    [EventHandler]
    public async Task HandleInitializedAsync(AgentProfileInitialized initialized)
    {
        ArgumentNullException.ThrowIfNull(initialized);
        ValidateIdentity(initialized.Identity);
        EnsureActorAddress(initialized.Identity.Owner);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, initialized.Identity.ProfileId, StringComparison.Ordinal));
        EnsureProfileContinuation(
            entry,
            initialized.Identity,
            initialized.SourceProfileActorId,
            initialized.SourceAuthorityStateVersion,
            initialized.ProvisioningOperationId,
            initialized.Operation);
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Provisioning)
            return;
        if (!string.Equals(entry.ProfileSlug, initialized.Identity.ProfileSlug, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent Profile initialization does not match namespace authority.");
        }

        var next = State.Clone();
        var updated = next.Profiles.First(x => x.ProfileId == entry.ProfileId);
        updated.Status = AgentProfileProvisioningStatus.Active;
        updated.ProvisioningTimeoutCallbackId = string.Empty;
        next.LastMutation = Outcome(FindOperation(entry.ProvisioningOperationId)!, AgentProfileMutationStatus.Succeeded,
            "PROFILE_ACTIVE", NextVersion());
        await PersistAsync(next, "provisioning-completed");
    }

    [EventHandler]
    public async Task HandleInitializationFailedAsync(AgentProfileInitializationFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ValidateIdentity(failed.Identity);
        EnsureActorAddress(failed.Identity.Owner);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, failed.Identity.ProfileId, StringComparison.Ordinal));
        EnsureProfileContinuation(
            entry,
            failed.Identity,
            failed.SourceProfileActorId,
            failed.SourceAuthorityStateVersion,
            failed.ProvisioningOperationId,
            failed.Operation);
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Provisioning)
            return;

        await FailProvisioningAsync(entry, "PROFILE_PROVISIONING_FAILED", "provisioning-failed");
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleProvisioningTimedOutAsync(AgentProfileProvisioningTimedOut timedOut)
    {
        ArgumentNullException.ThrowIfNull(timedOut);
        EnsureSelfTimeout(timedOut);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, timedOut.ProfileId, StringComparison.Ordinal));
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Provisioning ||
            !string.Equals(entry.ProvisioningOperationId, timedOut.ProvisioningOperationId, StringComparison.Ordinal) ||
            entry.ProvisioningAttempt != timedOut.ProvisioningAttempt ||
            !string.Equals(entry.ProvisioningTimeoutCallbackId, timedOut.CallbackId, StringComparison.Ordinal))
        {
            return;
        }

        await FailProvisioningAsync(entry, "PROFILE_PROVISIONING_TIMED_OUT", "provisioning-timed-out");
    }

    [EventHandler]
    public async Task HandleObservePublishedAsync(ObserveAgentProfilePublishedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentity(command.Identity);
        EnsureActorAddress(command.Identity.Owner);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, command.Identity.ProfileId, StringComparison.Ordinal));
        EnsurePublishedContinuation(entry, command);
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Active ||
            !AgentProfileDeterminism.SameOwner(State.Owner, command.Identity.Owner) ||
            !string.Equals(entry.ProfileSlug, command.Identity.ProfileSlug, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Published Profile summary does not match namespace authority.");
        }
        if (command.PublishedRevision < entry.PublishedRevision)
            return;
        if (command.PublishedRevision == entry.PublishedRevision &&
            entry.SnapshotSha256.Equals(command.SnapshotSha256))
        {
            return;
        }
        if (command.PublishedRevision == entry.PublishedRevision)
            throw new InvalidOperationException("Equal Profile revisions cannot carry conflicting snapshots.");

        var next = State.Clone();
        var updated = next.Profiles.First(x => x.ProfileId == entry.ProfileId);
        updated.DisplayName = command.DisplayName;
        updated.Purpose = command.Purpose;
        updated.PublishedRevision = command.PublishedRevision;
        updated.SnapshotSha256 = command.SnapshotSha256;
        await PersistAsync(next, "published-summary-observed");
    }

    [EventHandler]
    public async Task HandleSetDefaultBindingAsync(SetAgentProfileDefaultBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureActorAddress(command.Owner);
        var operation = CanonicalOperation(command.Operation, command);
        EnsureOwner(command.Owner);
        var namespaceOwner = State.Owner is null || State.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None
            ? command.Owner
            : State.Owner;
        if (TryHandleReplay(operation))
            return;
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(operation, command.Owner, "AUTHORITY_VERSION_CONFLICT");
            return;
        }
        if (!AgentProfilePolicies.IsSupportedAgentKind(command.AgentKind) ||
            !IsValidBindingTarget(command.Target))
        {
            await PersistRejectedAsync(operation, command.Owner, "BINDING_INVALID");
            return;
        }
        if (!IsValidBindingAdmission(namespaceOwner, command))
        {
            await PersistRejectedAsync(operation, command.Owner, "BINDING_ADMISSION_INVALID");
            return;
        }
        if (!IsAllowedBindingOwner(namespaceOwner, command.Target.Owner))
        {
            await PersistRejectedAsync(operation, command.Owner, "BINDING_TARGET_OWNER_INVALID");
            return;
        }

        if (AgentProfileDeterminism.SameOwner(namespaceOwner, command.Target.Owner) &&
            !TryValidateOwnedBindingTarget(command.Target, out var targetError))
        {
            await PersistRejectedAsync(operation, command.Owner, targetError);
            return;
        }

        var existingAuthorityBinding = State.DefaultBindings.FirstOrDefault(x => x.AgentKind == command.AgentKind);
        if (!TryBuildDefaultBinding(
                namespaceOwner,
                command,
                existingAuthorityBinding,
                out var nextBinding,
                out var rolloutError))
        {
            await PersistRejectedAsync(operation, command.Owner, rolloutError);
            return;
        }

        var next = State.Clone();
        if (next.Owner is null || next.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None)
            next.Owner = command.Owner.Clone();
        var existingBinding = next.DefaultBindings.FirstOrDefault(x => x.AgentKind == command.AgentKind);
        if (existingBinding is not null)
            next.DefaultBindings.Remove(existingBinding);
        next.DefaultBindings.Add(nextBinding);
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(operation, AgentProfileMutationStatus.Succeeded,
            "DEFAULT_BINDING_SET", NextVersion());
        await PersistAsync(next, "default-binding-set");
    }

    private bool TryValidateOwnedBindingTarget(AgentProfileBindingTarget target, out string error)
    {
        var profile = State.Profiles.FirstOrDefault(x => x.ProfileId == target.ProfileId);
        if (profile is null || profile.Status != AgentProfileProvisioningStatus.Active)
        {
            error = "PROFILE_NOT_FOUND";
            return false;
        }
        if (profile.PublishedRevision <= 0 || profile.SnapshotSha256.Length != 32 ||
            profile.PublishedRevision != target.PublishedRevision ||
            !profile.SnapshotSha256.Equals(target.SnapshotSha256))
        {
            error = "PROFILE_NOT_PUBLISHED";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidBindingTarget(AgentProfileBindingTarget? target) =>
        target?.Owner is not null &&
        target.Owner.OwnerCase != AgentProfileOwner.OwnerOneofCase.None &&
        !string.IsNullOrWhiteSpace(target.ProfileId) &&
        target.PublishedRevision > 0 &&
        target.SnapshotSha256.Length == 32;

    private static bool IsValidBindingAdmission(
        AgentProfileOwner owner,
        SetAgentProfileDefaultBindingCommand command) =>
        owner.OwnerCase switch
        {
            AgentProfileOwner.OwnerOneofCase.Scope =>
                command.AdmissionCase == SetAgentProfileDefaultBindingCommand.AdmissionOneofCase.Scope,
            AgentProfileOwner.OwnerOneofCase.System =>
                command.AdmissionCase == SetAgentProfileDefaultBindingCommand.AdmissionOneofCase.System &&
                AgentProfilePolicies.IsReviewedRolloutCohort(command.System.CohortBasisPoints),
            _ => false,
        };

    private static bool TryBuildDefaultBinding(
        AgentProfileOwner owner,
        SetAgentProfileDefaultBindingCommand command,
        AgentProfileDefaultBinding? existing,
        out AgentProfileDefaultBinding binding,
        out string error)
    {
        binding = new AgentProfileDefaultBinding
        {
            AgentKind = command.AgentKind,
            Target = command.Target.Clone(),
        };
        error = string.Empty;
        if (owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope)
        {
            binding.Scope = new AgentProfileScopeBindingAdmission();
            return true;
        }

        var admission = new AgentProfileSystemBindingAdmission
        {
            Enabled = command.System.Enabled,
            CohortBasisPoints = command.System.CohortBasisPoints,
        };
        if (existing is null)
        {
            if (admission.CohortBasisPoints != AgentProfilePolicies.FullCohortBasisPoints)
            {
                error = "ROLLOUT_BASELINE_REQUIRED";
                return false;
            }

            binding.System = admission;
            return true;
        }

        if (existing.AdmissionCase != AgentProfileDefaultBinding.AdmissionOneofCase.System ||
            existing.System is null ||
            !IsValidBindingTarget(existing.Target))
        {
            error = "ROLLOUT_STATE_INVALID";
            return false;
        }

        if (SameBindingTarget(existing.Target, command.Target))
        {
            if (admission.CohortBasisPoints != existing.System.CohortBasisPoints &&
                !IsNextRolloutStage(existing.System.CohortBasisPoints, admission.CohortBasisPoints))
            {
                error = "ROLLOUT_STAGE_INVALID";
                return false;
            }

            admission.PreviousReviewedTarget = existing.System.PreviousReviewedTarget?.Clone();
            if (admission.CohortBasisPoints != AgentProfilePolicies.FullCohortBasisPoints &&
                !IsValidBindingTarget(admission.PreviousReviewedTarget))
            {
                error = "ROLLOUT_BASELINE_REQUIRED";
                return false;
            }

            binding.System = admission;
            return true;
        }

        if (SameBindingTarget(existing.System.PreviousReviewedTarget, command.Target) &&
            admission.CohortBasisPoints == AgentProfilePolicies.FullCohortBasisPoints)
        {
            admission.PreviousReviewedTarget = existing.Target.Clone();
            binding.System = admission;
            return true;
        }

        if (existing.System.CohortBasisPoints == AgentProfilePolicies.FullCohortBasisPoints &&
            admission.CohortBasisPoints == AgentProfilePolicies.CanaryCohortBasisPoints)
        {
            admission.PreviousReviewedTarget = existing.Target.Clone();
            binding.System = admission;
            return true;
        }

        error = "ROLLOUT_STAGE_INVALID";
        return false;
    }

    private static bool IsNextRolloutStage(int current, int next) =>
        current == AgentProfilePolicies.CanaryCohortBasisPoints &&
        next == AgentProfilePolicies.ExpandedCohortBasisPoints ||
        current == AgentProfilePolicies.ExpandedCohortBasisPoints &&
        next == AgentProfilePolicies.FullCohortBasisPoints;

    private static bool SameBindingTarget(
        AgentProfileBindingTarget? left,
        AgentProfileBindingTarget? right) =>
        IsValidBindingTarget(left) &&
        IsValidBindingTarget(right) &&
        AgentProfileDeterminism.SameOwner(left!.Owner, right!.Owner) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.PublishedRevision == right.PublishedRevision &&
        left.SnapshotSha256.Equals(right.SnapshotSha256);

    private static bool IsAllowedBindingOwner(AgentProfileOwner namespaceOwner, AgentProfileOwner targetOwner) =>
        AgentProfileDeterminism.SameOwner(namespaceOwner, targetOwner) ||
        namespaceOwner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope &&
        targetOwner.OwnerCase == AgentProfileOwner.OwnerOneofCase.System &&
        string.Equals(targetOwner.System.PlatformId, AgentProfileOwners.PlatformId, StringComparison.Ordinal);

    [EventHandler]
    public async Task HandleClearDefaultBindingAsync(ClearAgentProfileDefaultBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        EnsureActorAddress(command.Owner);
        var operation = CanonicalOperation(command.Operation, command);
        EnsureOwner(command.Owner);
        if (TryHandleReplay(operation))
            return;
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(operation, command.Owner, "AUTHORITY_VERSION_CONFLICT");
            return;
        }
        if (!AgentProfilePolicies.IsSupportedAgentKind(command.AgentKind))
        {
            await PersistRejectedAsync(operation, command.Owner, "BINDING_INVALID");
            return;
        }

        var next = State.Clone();
        var existing = next.DefaultBindings.FirstOrDefault(x => x.AgentKind == command.AgentKind);
        if (existing is not null)
            next.DefaultBindings.Remove(existing);
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(operation,
            existing is null ? AgentProfileMutationStatus.NoChange : AgentProfileMutationStatus.Succeeded,
            existing is null ? "DEFAULT_BINDING_ABSENT" : "DEFAULT_BINDING_CLEARED",
            NextVersion());
        await PersistAsync(next, "default-binding-cleared");
    }

    protected override AgentProfileNamespaceState TransitionState(
        AgentProfileNamespaceState current,
        IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<AgentProfileNamespaceStateChangedEvent>(static (_, changed) => changed.State.Clone())
            .OrCurrent();

    private Task PersistAsync(AgentProfileNamespaceState state, string changeKind) =>
        PersistDomainEventAsync(new AgentProfileNamespaceStateChangedEvent
        {
            State = state.Clone(),
            ChangeKind = changeKind,
        });

    private Task SendInitializeAsync(
        AgentProfileCatalogEntry entry,
        AgentProfileOperationFact operation) =>
        SendToAsync(entry.ProfileActorId, new InitializeAgentProfileCommand
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = entry.ProfileId,
                Owner = State.Owner.Clone(),
                ProfileSlug = entry.ProfileSlug,
            },
            Operation = operation.Clone(),
        });

    private async Task RetryProvisioningAsync(
        AgentProfileCatalogEntry entry,
        AgentProfileOperationFact operation)
    {
        var next = State.Clone();
        var updated = next.Profiles.First(x =>
            string.Equals(x.ProfileId, entry.ProfileId, StringComparison.Ordinal));
        updated.Status = AgentProfileProvisioningStatus.Provisioning;
        updated.ProvisioningAttempt = checked(updated.ProvisioningAttempt + 1);
        updated.ProvisioningTimeoutCallbackId =
            BuildProvisioningTimeoutCallbackId(updated.ProfileId, updated.ProvisioningAttempt);
        next.LastMutation = Outcome(operation, AgentProfileMutationStatus.Succeeded,
            "PROFILE_PROVISIONING_RETRIED", NextVersion());
        await PersistAsync(next, "provisioning-retried");
        await ScheduleProvisioningTimeoutAsync(updated);
        await SendInitializeAsync(updated, operation);
    }

    private async Task FailProvisioningAsync(
        AgentProfileCatalogEntry entry,
        string outcomeCode,
        string changeKind)
    {
        var operation = FindOperation(entry.ProvisioningOperationId) ??
                        throw new InvalidOperationException(
                            "Agent Profile provisioning operation is missing from authority state.");
        var next = State.Clone();
        var updated = next.Profiles.First(x =>
            string.Equals(x.ProfileId, entry.ProfileId, StringComparison.Ordinal));
        updated.Status = AgentProfileProvisioningStatus.Failed;
        updated.ProvisioningTimeoutCallbackId = string.Empty;
        next.LastMutation = Outcome(
            operation,
            AgentProfileMutationStatus.Rejected,
            outcomeCode,
            NextVersion());
        await PersistAsync(next, changeKind);
    }

    private async Task ScheduleProvisioningTimeoutAsync(AgentProfileCatalogEntry entry)
    {
        await ScheduleSelfDurableTimeoutAsync(
            entry.ProvisioningTimeoutCallbackId,
            ProvisioningTimeout,
            new AgentProfileProvisioningTimedOut
            {
                ProfileId = entry.ProfileId,
                ProvisioningOperationId = entry.ProvisioningOperationId,
                ProvisioningAttempt = entry.ProvisioningAttempt,
                CallbackId = entry.ProvisioningTimeoutCallbackId,
            });
    }

    private async Task PersistRejectedAsync(
        AgentProfileOperationFact operation,
        AgentProfileOwner owner,
        string code)
    {
        var next = State.Clone();
        if (next.Owner is null || next.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None)
            next.Owner = owner.Clone();
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(operation, AgentProfileMutationStatus.Rejected, code, NextVersion());
        await PersistAsync(next, "rejected");
    }

    private bool TryHandleReplay(AgentProfileOperationFact operation)
    {
        var existing = FindOperation(operation.OperationId);
        if (existing is null)
            return false;
        EnsureOperationReplay(existing, operation);
        return true;
    }

    private AgentProfileOperationFact? FindOperation(string operationId) =>
        State.Operations.FirstOrDefault(x => x.OperationId == operationId);

    private void EnsureOwner(AgentProfileOwner owner)
    {
        if (State.Owner is null || State.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None)
            return;
        if (!AgentProfileDeterminism.SameOwner(State.Owner, owner))
            throw new InvalidOperationException("Agent Profile namespace owner cannot change.");
    }

    private static void ValidateOwner(AgentProfileOwner? owner)
    {
        if (owner is null || owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None)
            throw new InvalidOperationException("Agent Profile owner is required.");
        _ = AgentProfileActorIds.Namespace(owner);
    }

    private static void ValidateIdentity(AgentProfileIdentity? identity)
    {
        if (identity?.Owner is null || identity.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None ||
            string.IsNullOrWhiteSpace(identity.ProfileId) ||
            AgentProfilePolicies.ValidateProfileSlug(identity.ProfileSlug).Count > 0)
            throw new InvalidOperationException("A valid Agent Profile identity is required.");
        _ = AgentProfileActorIds.Namespace(identity.Owner);
    }

    private static void ValidateOperation(AgentProfileOperationFact? operation)
    {
        if (operation is null || string.IsNullOrWhiteSpace(operation.OperationId) ||
            string.IsNullOrWhiteSpace(operation.CommandId) ||
            string.IsNullOrWhiteSpace(operation.CorrelationId) ||
            operation.InputSha256.Length != 32)
            throw new InvalidOperationException("A complete Agent Profile operation fact is required.");
    }

    private static void EnsureOperationReplay(AgentProfileOperationFact existing, AgentProfileOperationFact incoming)
    {
        if (!existing.InputSha256.Equals(incoming.InputSha256))
            throw new InvalidOperationException("Agent Profile operation payload drift is not allowed.");
    }

    private static AgentProfileOperationFact CanonicalOperation(
        AgentProfileOperationFact? operation,
        IMessage command)
    {
        ValidateOperation(operation);
        var canonical = operation!.Clone();
        canonical.InputSha256 = AgentProfileDeterminism.ComputeSemanticCommandDigest(command);
        return canonical;
    }

    private void EnsureActorAddress(AgentProfileOwner owner)
    {
        if (!string.Equals(Id, AgentProfileActorIds.Namespace(owner), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Agent Profile namespace authority address does not match its typed identity.");
    }

    private void EnsureProfileContinuation(
        AgentProfileCatalogEntry? entry,
        AgentProfileIdentity identity,
        string sourceProfileActorId,
        long sourceAuthorityStateVersion,
        string provisioningOperationId,
        AgentProfileOperationFact operation)
    {
        ValidateOperation(operation);
        if (entry is null ||
            !AgentProfileDeterminism.SameOwner(State.Owner, identity.Owner) ||
            !string.Equals(entry.ProfileSlug, identity.ProfileSlug, StringComparison.Ordinal) ||
            !string.Equals(entry.ProfileActorId, sourceProfileActorId, StringComparison.Ordinal) ||
            !string.Equals(sourceProfileActorId, AgentProfileActorIds.Profile(identity.ProfileId), StringComparison.Ordinal) ||
            sourceAuthorityStateVersion <= 0 ||
            !string.Equals(entry.ProvisioningOperationId, provisioningOperationId, StringComparison.Ordinal) ||
            !string.Equals(operation.OperationId, provisioningOperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile continuation does not match namespace authority.");
        }

        var authorityOperation = FindOperation(provisioningOperationId);
        if (authorityOperation is null ||
            !string.Equals(authorityOperation.CommandId, operation.CommandId, StringComparison.Ordinal) ||
            !string.Equals(authorityOperation.CorrelationId, operation.CorrelationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile continuation operation does not match namespace authority.");
        }

        EnsurePublisher(sourceProfileActorId);
    }

    private void EnsurePublishedContinuation(
        AgentProfileCatalogEntry? entry,
        ObserveAgentProfilePublishedCommand command)
    {
        if (entry is null ||
            !string.Equals(entry.ProfileActorId, command.SourceProfileActorId, StringComparison.Ordinal) ||
            !string.Equals(
                command.SourceProfileActorId,
                AgentProfileActorIds.Profile(command.Identity.ProfileId),
                StringComparison.Ordinal) ||
            command.SourceAuthorityStateVersion <= 0 ||
            string.IsNullOrWhiteSpace(command.SourceOperationId))
        {
            throw new InvalidOperationException(
                "Published Profile continuation does not match namespace authority.");
        }

        EnsurePublisher(command.SourceProfileActorId);
    }

    private void EnsureSelfTimeout(AgentProfileProvisioningTimedOut timedOut)
    {
        var envelope = ActiveInboundEnvelope;
        var callback = envelope?.Runtime?.Callback;
        if (string.IsNullOrWhiteSpace(timedOut.CallbackId) || timedOut.ProvisioningAttempt <= 0 ||
            !string.Equals(envelope?.Route?.PublisherActorId, Id, StringComparison.Ordinal) ||
            envelope?.Route.GetTopologyAudience() != TopologyAudience.Self ||
            callback is null ||
            !string.Equals(callback.CallbackId, timedOut.CallbackId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile provisioning timeout is not a valid durable self callback.");
        }
    }

    private void EnsurePublisher(string expectedPublisherActorId)
    {
        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId ?? string.Empty;
        if (!string.Equals(publisherActorId, expectedPublisherActorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent Profile continuation publisher does not match authority.");
    }

    private static string BuildProvisioningTimeoutCallbackId(string profileId, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var digest = AgentProfileDeterminism.Sha256Utf8(profileId.Trim());
        return $"agent-profile-provisioning-{Convert.ToHexStringLower(digest.AsSpan(0, 16))}-{attempt}";
    }

    private long CurrentVersion() => EventSourcing?.CurrentVersion ?? 0;

    private long NextVersion() => checked(CurrentVersion() + 1);

    private static AgentProfileMutationOutcome Outcome(
        AgentProfileOperationFact operation,
        AgentProfileMutationStatus status,
        string code,
        long authorityVersion) => new()
    {
        Operation = operation.Clone(),
        Status = status,
        Code = code,
        AuthorityStateVersion = authorityVersion,
        RecordedAt = operation.RequestedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };
}
