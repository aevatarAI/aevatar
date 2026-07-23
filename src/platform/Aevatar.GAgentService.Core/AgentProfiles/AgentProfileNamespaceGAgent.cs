using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.AgentProfiles;

[GAgent("gagent.service.agent-profile-namespace")]
public sealed class AgentProfileNamespaceGAgent : GAgentBase<AgentProfileNamespaceState>
{
    public AgentProfileNamespaceGAgent() => InitializeId();

    [EventHandler]
    public async Task HandleCreateAsync(CreateAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var profileActorId = AgentProfileActorInvariants.RequireActorId(
            command.ProfileActorId,
            "profile_actor_id");
        var rejectedSemanticInputSha256 = ComputeCreateSemanticInputSha256(command, profileActorId);
        var existingOperation = FindOperation(operation.OperationId);

        var identityDiagnostics = AgentProfilePolicies.ValidateIdentity(command.Identity);
        if (identityDiagnostics.Count > 0)
        {
            if (existingOperation is not null)
            {
                EnsureCreateValidationRejectionReplay(
                    existingOperation,
                    operation,
                    profileActorId,
                    rejectedSemanticInputSha256);
                return;
            }
            await PersistCreateFailureAsync(
                operation,
                null,
                profileActorId,
                identityDiagnostics[0],
                rejectedSemanticInputSha256);
            return;
        }

        var identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);

        var contentDiagnostics = AgentProfilePolicies.ValidateContent(command.InitialContent);
        if (contentDiagnostics.Count > 0)
        {
            if (existingOperation is not null)
            {
                EnsureCreateValidationRejectionReplay(
                    existingOperation,
                    operation,
                    profileActorId,
                    rejectedSemanticInputSha256);
                return;
            }
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                contentDiagnostics[0],
                rejectedSemanticInputSha256);
            return;
        }

        var content = AgentProfileDeterminism.NormalizeContent(command.InitialContent);
        var expectedInput = AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content);
        if (existingOperation is not null)
        {
            EnsureReplayInput(existingOperation.Operation, operation, expectedInput);
            if (!string.Equals(existingOperation.ProfileActorId, profileActorId, StringComparison.Ordinal))
            {
                throw AgentProfileActorInvariants.Error(
                    "PROFILE_PROVISIONING_CONTINUATION_MISMATCH",
                    "An operation cannot change its Profile Actor target.");
            }
            if (!existingOperation.ProvisioningStarted)
            {
                if (existingOperation.Diagnostic is not null)
                    return;
                throw AgentProfileActorInvariants.Error(
                    "UNKNOWN_PROFILE_PROVISIONING",
                    "The operation did not commit Profile provisioning.");
            }

            var replayEntry = FindProfile(existingOperation.ProfileId);
            if (replayEntry is null)
            {
                if (existingOperation.Diagnostic is not null)
                    return;
                throw AgentProfileActorInvariants.Error(
                    "UNKNOWN_PROFILE_PROVISIONING",
                    "The operation has no durable Profile provisioning entry.");
            }
            if (replayEntry.Status is AgentProfileProvisioningStatus.Provisioning or
                AgentProfileProvisioningStatus.Failed)
            {
                await SendInitializationAsync(replayEntry, operation);
            }
            return;
        }

        if (!AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.InputDigestMismatch());
            return;
        }

        var ownerClaim = State.HandleClaims.FirstOrDefault(claim =>
            AgentProfileActorInvariants.SameOwner(claim.Owner, identity.Owner));
        if (ownerClaim is not null &&
            !string.Equals(ownerClaim.OwnerHandle, identity.Reference.OwnerHandle, StringComparison.Ordinal))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.Diagnostic(
                    "OWNER_HANDLE_CONFLICT",
                    "The owner already has a different committed handle.",
                    "identity.reference.owner_handle"));
            return;
        }

        var handleClaim = State.HandleClaims.FirstOrDefault(claim =>
            string.Equals(claim.OwnerHandle, identity.Reference.OwnerHandle, StringComparison.Ordinal));
        if (handleClaim is not null &&
            !AgentProfileActorInvariants.SameOwner(handleClaim.Owner, identity.Owner))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.Diagnostic(
                    "OWNER_HANDLE_CONFLICT",
                    "The requested owner handle is already claimed.",
                    "identity.reference.owner_handle"));
            return;
        }

        if (State.Profiles.Any(entry =>
                string.Equals(entry.Identity?.ProfileId, identity.ProfileId, StringComparison.Ordinal)))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.Diagnostic(
                    "PROFILE_ID_TAKEN",
                    "The Profile identity is already claimed.",
                    "identity.profile_id"));
            return;
        }

        if (State.Profiles.Any(entry =>
                string.Equals(entry.ProfileActorId, profileActorId, StringComparison.Ordinal)))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.Diagnostic(
                    "PROFILE_ACTOR_ID_TAKEN",
                    "The Profile Actor identity is already claimed.",
                    "profile_actor_id"));
            return;
        }

        if (State.Profiles.Any(entry =>
                AgentProfileActorInvariants.SameReference(entry.Identity?.Reference, identity.Reference)))
        {
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                AgentProfileActorInvariants.Diagnostic(
                    "PROFILE_SLUG_TAKEN",
                    "The human Profile reference is already claimed.",
                    "identity.reference.profile_slug"));
            return;
        }

        await PersistDomainEventAsync(new AgentProfileProvisioningStartedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            InitialContent = content.Clone(),
            ProfileActorId = profileActorId,
        });

        await SendInitializationAsync(
            FindProfile(identity.ProfileId)
                ?? throw AgentProfileActorInvariants.Error(
                    "UNKNOWN_PROFILE_PROVISIONING",
                    "The committed Profile provisioning entry is unavailable."),
            operation);
    }

    [EventHandler]
    public async Task HandleInitializedAsync(AgentProfileInitializedContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
        var entry = RequireContinuationEntry(
            continuation.Identity,
            continuation.ProfileActorId,
            operation);
        var expectedDraftSha256 = AgentProfileDeterminism.ComputeDraftSha256(entry.InitialContent);
        if (continuation.DraftRevision != 1 ||
            !AgentProfileActorInvariants.DigestEquals(
                continuation.DraftSha256,
                expectedDraftSha256))
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PROVISIONING_CONTINUATION_MISMATCH",
                "The initialization continuation does not match the committed initial draft.");
        }

        if (entry.Status == AgentProfileProvisioningStatus.Active)
            return;

        await PersistDomainEventAsync(new AgentProfileProvisioningCompletedEvent
        {
            Operation = operation.Clone(),
            Identity = entry.Identity.Clone(),
            ProfileActorId = entry.ProfileActorId,
        });
    }

    [EventHandler]
    public async Task HandleInitializationRejectedAsync(
        AgentProfileInitializationRejectedContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
        var entry = RequireContinuationEntry(
            continuation.Identity,
            continuation.ProfileActorId,
            operation);
        if (continuation.Diagnostic is null || string.IsNullOrWhiteSpace(continuation.Diagnostic.Code))
        {
            throw AgentProfileActorInvariants.Error(
                "INVALID_PROFILE_INITIALIZATION_REJECTION",
                "A typed initialization rejection diagnostic is required.");
        }

        if (entry.Status == AgentProfileProvisioningStatus.Active)
            return;
        if (entry.Status == AgentProfileProvisioningStatus.Failed)
        {
            if (entry.Failure?.Equals(continuation.Diagnostic) == true)
                return;
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PROVISIONING_CONTINUATION_MISMATCH",
                "A failed provisioning operation cannot change its committed diagnostic.");
        }

        await PersistDomainEventAsync(new AgentProfileProvisioningFailedEvent
        {
            Operation = operation.Clone(),
            Identity = entry.Identity.Clone(),
            ProfileActorId = entry.ProfileActorId,
            Diagnostic = continuation.Diagnostic.Clone(),
            FailureKind = AgentProfileProvisioningFailureKind.InitializationContinuation,
        });
    }

    [EventHandler]
    public async Task HandleObservePublishedSummaryAsync(
        ObserveAgentProfilePublishedSummaryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        AgentProfileIdentity identity;
        try
        {
            identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);
        }
        catch (AgentProfileContractValidationException)
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                "The published summary identity is invalid.");
        }

        var entry = FindProfile(identity.ProfileId);
        if (entry is null ||
            entry.Status != AgentProfileProvisioningStatus.Active ||
            !AgentProfileActorInvariants.SameIdentity(entry.Identity, identity) ||
            command.Summary is null ||
            !AgentProfileActorInvariants.SameReference(
                command.Summary.Reference,
                entry.Identity.Reference) ||
            command.Summary.SnapshotSha256.Length != 32)
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                "The published summary does not belong to the mapped Profile.");
        }

        var existingOperation = FindOperation(operation.OperationId);
        if (existingOperation is not null)
        {
            EnsureReplayInput(existingOperation.Operation, operation);
            if (existingOperation.PublishedSummary is null ||
                !AgentProfileActorInvariants.SameSummary(
                    existingOperation.PublishedSummary,
                    command.Summary))
            {
                throw AgentProfileActorInvariants.Error(
                    "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                    "A published-summary operation cannot change its summary.");
            }
            return;
        }

        var current = entry.PublishedSummary;
        if (current is not null)
        {
            if (command.Summary.PublishedRevision < current.PublishedRevision)
                return;
            if (command.Summary.PublishedRevision == current.PublishedRevision)
            {
                if (AgentProfileActorInvariants.SameSummary(current, command.Summary))
                    return;
                throw AgentProfileActorInvariants.Error(
                    "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                    "The same published revision cannot carry a different summary.");
            }
        }
        else if (command.Summary.PublishedRevision <= 0)
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                "The first published summary revision must be positive.");
        }

        await PersistDomainEventAsync(new AgentProfilePublishedSummaryObservedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            Summary = command.Summary.Clone(),
        });
    }

    protected override AgentProfileNamespaceState TransitionState(
        AgentProfileNamespaceState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentProfileProvisioningStartedEvent>(ApplyProvisioningStarted)
            .On<AgentProfileProvisioningCompletedEvent>(ApplyProvisioningCompleted)
            .On<AgentProfileProvisioningFailedEvent>(ApplyProvisioningFailed)
            .On<AgentProfilePublishedSummaryObservedEvent>(ApplyPublishedSummaryObserved)
            .OrCurrent();

    private async Task PersistCreateFailureAsync(
        AgentProfileOperationFact operation,
        AgentProfileIdentity? identity,
        string profileActorId,
        AgentProfileSafeDiagnostic diagnostic,
        ByteString? rejectedSemanticInputSha256 = null) =>
        await PersistDomainEventAsync(new AgentProfileProvisioningFailedEvent
        {
            Operation = operation.Clone(),
            Identity = identity?.Clone() ?? new AgentProfileIdentity(),
            ProfileActorId = profileActorId,
            Diagnostic = diagnostic.Clone(),
            RejectedSemanticInputSha256 = rejectedSemanticInputSha256 ?? ByteString.Empty,
            FailureKind = AgentProfileProvisioningFailureKind.CreateValidation,
        });

    private Task SendInitializationAsync(
        AgentProfileNamespaceEntryState entry,
        AgentProfileOperationFact operation) =>
        SendToAsync(
            entry.ProfileActorId,
            new InitializeAgentProfileCommand
            {
                Operation = operation.Clone(),
                Identity = entry.Identity.Clone(),
                InitialContent = entry.InitialContent.Clone(),
                NamespaceActorId = Id,
            },
            CancellationToken.None);

    private AgentProfileNamespaceEntryState RequireContinuationEntry(
        AgentProfileIdentity? identity,
        string? profileActorId,
        AgentProfileOperationFact operation)
    {
        var entry = FindProfile(identity?.ProfileId ?? string.Empty)
            ?? throw AgentProfileActorInvariants.Error(
                "UNKNOWN_PROFILE_PROVISIONING",
                "No durable provisioning entry matches the continuation.");
        var storedOperation = FindOperation(operation.OperationId);
        if (storedOperation is null ||
            !storedOperation.ProvisioningStarted ||
            !AgentProfileActorInvariants.SameInput(storedOperation.Operation, operation) ||
            !AgentProfileActorInvariants.SameIdentity(entry.Identity, identity) ||
            !string.Equals(entry.ProfileActorId, profileActorId, StringComparison.Ordinal) ||
            !string.Equals(storedOperation.ProfileId, entry.Identity.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(storedOperation.ProfileActorId, entry.ProfileActorId, StringComparison.Ordinal))
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PROVISIONING_CONTINUATION_MISMATCH",
                "The continuation does not match the durable provisioning operation.");
        }
        return entry;
    }

    private AgentProfileNamespaceEntryState? FindProfile(string profileId) =>
        State.Profiles.FirstOrDefault(entry =>
            string.Equals(entry.Identity?.ProfileId, profileId, StringComparison.Ordinal));

    private AgentProfileNamespaceOperationState? FindOperation(string operationId) =>
        State.Operations.FirstOrDefault(entry =>
            string.Equals(entry.Operation?.OperationId, operationId, StringComparison.Ordinal));

    private static void EnsureReplayInput(
        AgentProfileOperationFact existing,
        AgentProfileOperationFact candidate,
        ByteString? expectedInput = null)
    {
        if (!AgentProfileActorInvariants.SameInput(existing, candidate) ||
            expectedInput is not null &&
            !AgentProfileActorInvariants.DigestEquals(candidate.InputSha256, expectedInput))
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An operation id cannot be reused with a different normalized input.");
        }
    }

    private static void EnsureCreateValidationRejectionReplay(
        AgentProfileNamespaceOperationState existing,
        AgentProfileOperationFact candidate,
        string profileActorId,
        ByteString rejectedSemanticInputSha256)
    {
        if (existing.FailureKind != AgentProfileProvisioningFailureKind.CreateValidation ||
            existing.ProvisioningStarted ||
            existing.Diagnostic is null ||
            !AgentProfileActorInvariants.SameInput(existing.Operation, candidate) ||
            !string.Equals(existing.ProfileActorId, profileActorId, StringComparison.Ordinal) ||
            !AgentProfileActorInvariants.DigestEquals(
                existing.RejectedSemanticInputSha256,
                rejectedSemanticInputSha256))
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "A rejected create operation cannot change its typed semantic input or Actor relation.");
        }
    }

    private static ByteString ComputeCreateSemanticInputSha256(
        CreateAgentProfileCommand command,
        string profileActorId)
    {
        var material = new AgentProfileCreateSemanticInputFingerprintMaterial
        {
            ProfileActorId = profileActorId,
        };
        if (command.Identity is not null)
            material.Identity = command.Identity.Clone();
        if (command.InitialContent is not null)
            material.InitialContent = command.InitialContent.Clone();
        return AgentProfileDeterminism.Sha256(material);
    }

    private static AgentProfileNamespaceState ApplyProvisioningStarted(
        AgentProfileNamespaceState state,
        AgentProfileProvisioningStartedEvent evt)
    {
        var next = state.Clone();
        if (!next.HandleClaims.Any(claim =>
                AgentProfileActorInvariants.SameOwner(claim.Owner, evt.Identity.Owner)))
        {
            next.HandleClaims.Add(new AgentProfileOwnerHandleClaimState
            {
                OwnerHandle = evt.Identity.Reference.OwnerHandle,
                Owner = evt.Identity.Owner.Clone(),
            });
        }

        next.Profiles.Add(new AgentProfileNamespaceEntryState
        {
            Identity = evt.Identity.Clone(),
            ProfileActorId = evt.ProfileActorId,
            Status = AgentProfileProvisioningStatus.Provisioning,
            InitialContent = evt.InitialContent.Clone(),
        });
        next.Operations.Add(new AgentProfileNamespaceOperationState
        {
            Operation = evt.Operation.Clone(),
            ProfileId = evt.Identity.ProfileId,
            ProfileActorId = evt.ProfileActorId,
            ProvisioningStarted = true,
        });
        return next;
    }

    private static AgentProfileNamespaceState ApplyProvisioningCompleted(
        AgentProfileNamespaceState state,
        AgentProfileProvisioningCompletedEvent evt)
    {
        var next = state.Clone();
        var entry = next.Profiles.First(profile =>
            string.Equals(profile.Identity.ProfileId, evt.Identity.ProfileId, StringComparison.Ordinal));
        entry.Status = AgentProfileProvisioningStatus.Active;
        entry.Failure = null;
        return next;
    }

    private static AgentProfileNamespaceState ApplyProvisioningFailed(
        AgentProfileNamespaceState state,
        AgentProfileProvisioningFailedEvent evt)
    {
        var next = state.Clone();
        var operation = next.Operations.FirstOrDefault(candidate =>
            string.Equals(candidate.Operation.OperationId, evt.Operation.OperationId, StringComparison.Ordinal));
        if (evt.FailureKind == AgentProfileProvisioningFailureKind.InitializationContinuation &&
            operation is not null &&
            operation.ProvisioningStarted &&
            string.Equals(operation.ProfileId, evt.Identity.ProfileId, StringComparison.Ordinal) &&
            string.Equals(operation.ProfileActorId, evt.ProfileActorId, StringComparison.Ordinal))
        {
            var entry = next.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Identity.ProfileId, evt.Identity.ProfileId, StringComparison.Ordinal) &&
                string.Equals(profile.ProfileActorId, evt.ProfileActorId, StringComparison.Ordinal));
            if (entry is not null)
            {
                entry.Status = AgentProfileProvisioningStatus.Failed;
                entry.Failure = evt.Diagnostic.Clone();
            }
        }

        if (operation is null)
        {
            next.Operations.Add(new AgentProfileNamespaceOperationState
            {
                Operation = evt.Operation.Clone(),
                ProfileId = evt.Identity.ProfileId,
                ProfileActorId = evt.ProfileActorId,
                Diagnostic = evt.Diagnostic.Clone(),
                RejectedSemanticInputSha256 = evt.RejectedSemanticInputSha256,
                FailureKind = evt.FailureKind,
            });
        }
        else if (evt.FailureKind == AgentProfileProvisioningFailureKind.InitializationContinuation &&
                 operation is not null &&
                 operation.ProvisioningStarted &&
                 string.Equals(operation.ProfileId, evt.Identity.ProfileId, StringComparison.Ordinal) &&
                 string.Equals(operation.ProfileActorId, evt.ProfileActorId, StringComparison.Ordinal))
        {
            operation.Diagnostic = evt.Diagnostic.Clone();
            operation.FailureKind = evt.FailureKind;
        }
        return next;
    }

    private static AgentProfileNamespaceState ApplyPublishedSummaryObserved(
        AgentProfileNamespaceState state,
        AgentProfilePublishedSummaryObservedEvent evt)
    {
        var next = state.Clone();
        var entry = next.Profiles.First(profile =>
            string.Equals(profile.Identity.ProfileId, evt.Identity.ProfileId, StringComparison.Ordinal));
        entry.PublishedSummary = evt.Summary.Clone();
        next.Operations.Add(new AgentProfileNamespaceOperationState
        {
            Operation = evt.Operation.Clone(),
            ProfileId = evt.Identity.ProfileId,
            ProfileActorId = entry.ProfileActorId,
            PublishedSummary = evt.Summary.Clone(),
        });
        return next;
    }
}
