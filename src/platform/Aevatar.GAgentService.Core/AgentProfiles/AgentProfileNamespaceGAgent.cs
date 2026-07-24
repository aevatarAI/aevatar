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
    private readonly IAgentProfileIngressProofVerifier _ingressProofVerifier;

    public AgentProfileNamespaceGAgent(IAgentProfileIngressProofVerifier ingressProofVerifier)
    {
        _ingressProofVerifier = ingressProofVerifier ??
            throw new ArgumentNullException(nameof(ingressProofVerifier));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIngressProof(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var profileActorId = AgentProfileActorInvariants.RequireActorId(
            command.ProfileActorId,
            "profile_actor_id");
        var precanonicalReplayAuthority = AgentProfileActorInvariants.PrecanonicalReplayAuthority(
            AgentProfileOperationKind.Create,
            Id,
            profileActorId,
            ComputeCreateSemanticInputSha256(command, profileActorId));
        var existingOperation = FindOperation(operation.OperationId);

        var identityDiagnostics = AgentProfilePolicies.ValidateIdentity(command.Identity);
        if (identityDiagnostics.Count > 0)
        {
            if (existingOperation is not null)
            {
                EnsureCreateValidationRejectionReplay(
                    existingOperation,
                    precanonicalReplayAuthority);
                return;
            }
            await PersistCreateFailureAsync(
                operation,
                null,
                profileActorId,
                identityDiagnostics[0],
                precanonicalReplayAuthority);
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
                    precanonicalReplayAuthority);
                return;
            }
            await PersistCreateFailureAsync(
                operation,
                identity,
                profileActorId,
                contentDiagnostics[0],
                precanonicalReplayAuthority);
            return;
        }

        var content = AgentProfileDeterminism.NormalizeContent(command.InitialContent);
        var expectedInput = AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content);
        var replayAuthority = AgentProfileActorInvariants.CanonicalReplayAuthority(
            AgentProfileOperationKind.Create,
            Id,
            profileActorId,
            expectedInput);
        if (existingOperation is not null)
        {
            AgentProfileActorInvariants.EnsureSameReplayAuthority(
                existingOperation.ReplayAuthority,
                replayAuthority,
                "An operation id cannot be reused with a different normalized input.");
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
                AgentProfileActorInvariants.InputDigestMismatch(),
                replayAuthority);
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
                    "identity.reference.owner_handle"),
                replayAuthority);
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
                    "identity.reference.owner_handle"),
                replayAuthority);
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
                    "identity.profile_id"),
                replayAuthority);
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
                    "profile_actor_id"),
                replayAuthority);
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
                    "identity.reference.profile_slug"),
                replayAuthority);
            return;
        }

        await PersistDomainEventAsync(new AgentProfileProvisioningStartedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            InitialContent = content.Clone(),
            ProfileActorId = profileActorId,
            ReplayAuthority = replayAuthority.Clone(),
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
        var profileActorId = AgentProfileActorInvariants.RequireActorId(
            continuation.ProfileActorId,
            "profile_actor_id");
        AgentProfileActorInvariants.RequireProtocolPublisher(
            ActiveInboundEnvelope,
            profileActorId);
        var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
        var entry = RequireContinuationEntry(
            continuation.Identity,
            profileActorId,
            operation);
        var storedOperation = FindOperation(operation.OperationId)!;
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
            ReplayAuthority = storedOperation.ReplayAuthority.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleInitializationRejectedAsync(
        AgentProfileInitializationRejectedContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var profileActorId = AgentProfileActorInvariants.RequireActorId(
            continuation.ProfileActorId,
            "profile_actor_id");
        AgentProfileActorInvariants.RequireProtocolPublisher(
            ActiveInboundEnvelope,
            profileActorId);
        var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
        var entry = RequireContinuationEntry(
            continuation.Identity,
            profileActorId,
            operation);
        if (continuation.Diagnostic is null || string.IsNullOrWhiteSpace(continuation.Diagnostic.Code))
        {
            throw AgentProfileActorInvariants.Error(
                "INVALID_PROFILE_INITIALIZATION_REJECTION",
                "A typed initialization rejection diagnostic is required.");
        }
        var diagnostic = AgentProfilePolicies.NormalizeDiagnostic(continuation.Diagnostic);
        var storedOperation = FindOperation(operation.OperationId)!;

        if (entry.Status == AgentProfileProvisioningStatus.Active)
            return;
        if (entry.Status == AgentProfileProvisioningStatus.Failed)
        {
            if (entry.Failure?.Equals(diagnostic) == true)
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
            Diagnostic = diagnostic,
            ReplayAuthority = storedOperation.ReplayAuthority.Clone(),
            FailureKind = AgentProfileProvisioningFailureKind.InitializationContinuation,
        });
    }

    [EventHandler]
    public async Task HandleObservePublishedSummaryAsync(
        ObserveAgentProfilePublishedSummaryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileIdentity identity;
        AgentProfilePublishedSummary summary;
        try
        {
            identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);
            summary = AgentProfileDeterminism.NormalizePublishedSummary(command.Summary);
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
            !AgentProfileActorInvariants.SameReference(
                summary.Reference,
                entry.Identity.Reference))
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                "The published summary does not belong to the mapped Profile.");
        }
        AgentProfileActorInvariants.RequireProtocolPublisher(
            ActiveInboundEnvelope,
            entry.ProfileActorId);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var replayAuthority = AgentProfileActorInvariants.CanonicalReplayAuthority(
            AgentProfileOperationKind.ObservePublishedSummary,
            Id,
            entry.ProfileActorId,
            AgentProfileDeterminism.Sha256(
                new AgentProfilePublishedSummarySemanticInputFingerprintMaterial
                {
                    Identity = identity.Clone(),
                    Summary = summary.Clone(),
                }));

        var existingOperation = FindOperation(operation.OperationId);
        if (existingOperation is not null)
        {
            AgentProfileActorInvariants.EnsureSameReplayAuthority(
                existingOperation.ReplayAuthority,
                replayAuthority,
                "An operation id cannot be reused with a different published summary.");
            if (existingOperation.PublishedSummary is null ||
                !AgentProfileActorInvariants.SameSummary(
                    existingOperation.PublishedSummary,
                    summary))
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
            if (summary.PublishedRevision < current.PublishedRevision)
                return;
            if (summary.PublishedRevision == current.PublishedRevision)
            {
                if (AgentProfileActorInvariants.SameSummary(current, summary))
                    return;
                throw AgentProfileActorInvariants.Error(
                    "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                    "The same published revision cannot carry a different summary.");
            }
        }
        else if (summary.PublishedRevision <= 0)
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                "The first published summary revision must be positive.");
        }

        await PersistDomainEventAsync(new AgentProfilePublishedSummaryObservedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            Summary = summary,
            ReplayAuthority = replayAuthority,
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
        AgentProfileOperationReplayAuthority replayAuthority) =>
        await PersistDomainEventAsync(new AgentProfileProvisioningFailedEvent
        {
            Operation = operation.Clone(),
            Identity = identity?.Clone() ?? new AgentProfileIdentity(),
            ProfileActorId = profileActorId,
            Diagnostic = AgentProfilePolicies.NormalizeDiagnostic(diagnostic),
            ReplayAuthority = replayAuthority.Clone(),
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
            !AgentProfileActorInvariants.SameIdentity(entry.Identity, identity) ||
            !string.Equals(entry.ProfileActorId, profileActorId, StringComparison.Ordinal) ||
            !string.Equals(storedOperation.ProfileId, entry.Identity.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(storedOperation.ProfileActorId, entry.ProfileActorId, StringComparison.Ordinal))
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_PROVISIONING_CONTINUATION_MISMATCH",
                "The continuation does not match the durable provisioning operation.");
        }
        var replayAuthority = AgentProfileActorInvariants.CanonicalReplayAuthority(
            AgentProfileOperationKind.Create,
            Id,
            entry.ProfileActorId,
            AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(
                entry.Identity,
                entry.InitialContent));
        AgentProfileActorInvariants.EnsureSameReplayAuthority(
            storedOperation.ReplayAuthority,
            replayAuthority,
            "The continuation operation does not match the durable provisioning authority.");
        return entry;
    }

    private AgentProfileNamespaceEntryState? FindProfile(string profileId) =>
        State.Profiles.FirstOrDefault(entry =>
            string.Equals(entry.Identity?.ProfileId, profileId, StringComparison.Ordinal));

    private AgentProfileNamespaceOperationState? FindOperation(string operationId) =>
        State.Operations.FirstOrDefault(entry =>
            string.Equals(entry.Operation?.OperationId, operationId, StringComparison.Ordinal));

    private void RequireIngressProof(IMessage command)
    {
        if (!_ingressProofVerifier.Verify(Id, command))
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_INGRESS_PROOF_INVALID",
                "The Agent Profile command ingress proof is invalid.");
        }
    }

    private static void EnsureCreateValidationRejectionReplay(
        AgentProfileNamespaceOperationState existing,
        AgentProfileOperationReplayAuthority replayAuthority)
    {
        if (existing.FailureKind != AgentProfileProvisioningFailureKind.CreateValidation ||
            existing.ProvisioningStarted ||
            existing.Diagnostic is null)
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "A rejected create operation cannot change its typed semantic input or Actor relation.");
        }
        AgentProfileActorInvariants.EnsureSameReplayAuthority(
            existing.ReplayAuthority,
            replayAuthority,
            "A rejected create operation cannot change its typed semantic input or Actor relation.");
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
        else
            material.IdentityMissing = true;
        if (command.InitialContent is not null)
            material.InitialContent = command.InitialContent.Clone();
        else
            material.InitialContentMissing = true;
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
            ReplayAuthority = evt.ReplayAuthority.Clone(),
        });
        CompactOperations(next);
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
        CompactOperations(next);
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
                ReplayAuthority = evt.ReplayAuthority.Clone(),
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
        CompactOperations(next);
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
            ReplayAuthority = evt.ReplayAuthority.Clone(),
        });
        CompactOperations(next);
        return next;
    }

    private static void CompactOperations(AgentProfileNamespaceState state)
    {
        var pinnedTargets = state.Profiles
            .Where(profile => profile.Status is
                AgentProfileProvisioningStatus.Provisioning or
                AgentProfileProvisioningStatus.Failed)
            .Select(profile => (profile.Identity?.ProfileId ?? string.Empty, profile.ProfileActorId))
            .ToHashSet();
        var terminalCount = state.Operations.Count(operation => !IsPinned(operation, pinnedTargets));
        var removeCount = terminalCount -
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations;
        for (var index = 0; removeCount > 0 && index < state.Operations.Count;)
        {
            if (IsPinned(state.Operations[index], pinnedTargets))
            {
                index++;
                continue;
            }

            state.Operations.RemoveAt(index);
            removeCount--;
        }
    }

    private static bool IsPinned(
        AgentProfileNamespaceOperationState operation,
        HashSet<(string ProfileId, string ProfileActorId)> pinnedTargets) =>
        operation.ProvisioningStarted &&
        pinnedTargets.Contains((operation.ProfileId, operation.ProfileActorId));
}
