using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.AgentProfiles;

[GAgent("gagent.agent-profile")]
public sealed class AgentProfileGAgent : GAgentBase<AgentProfileState>, IProjectedActor
{
    public const string DurableProjectionKind = "agent-profile-current-state";

    public static string ProjectionKind => DurableProjectionKind;

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentity(command.Identity);
        EnsureActorAddress(command.Identity.ProfileId);
        EnsurePublisher(AgentProfileActorIds.Namespace(command.Identity.Owner));
        var operation = CanonicalOperation(command.Operation, command);

        if (FindOperation(operation.OperationId) is { } existing)
        {
            EnsureOperationReplay(existing, operation);
            await SendInitializedAsync(State.Identity, existing, CurrentVersion());
            return;
        }

        if (State.Identity is not null && !string.IsNullOrWhiteSpace(State.Identity.ProfileId))
        {
            await PersistRejectedAsync(operation, "PROFILE_ALREADY_INITIALIZED");
            await SendInitializationFailedAsync(command.Identity, operation, "PROFILE_ALREADY_INITIALIZED");
            return;
        }

        var next = State.Clone();
        next.Identity = command.Identity.Clone();
        next.NamespaceActorId = AgentProfileActorIds.Namespace(command.Identity.Owner);
        if (command.InitialDraft is not null)
        {
            next.Draft = AgentProfileDeterminism.NormalizeDraft(command.InitialDraft);
            next.DraftRevision = 1;
            next.DraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(next.Draft);
        }
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(
            operation,
            AgentProfileMutationStatus.Succeeded,
            "PROFILE_INITIALIZED",
            NextVersion(),
            next.DraftRevision,
            next.PublishedRevision);
        await PersistAsync(next, "initialized");
        await SendInitializedAsync(next.Identity, operation, CurrentVersion());
    }

    [EventHandler]
    public async Task HandleUpdateDraftAsync(UpdateAgentProfileDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity);
        var operation = CanonicalOperation(command.Operation, command);
        if (TryHandleReplay(operation))
            return;
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(operation, "AUTHORITY_VERSION_CONFLICT");
            return;
        }

        var diagnostics = AgentProfilePolicies.ValidateDraft(command.Draft);
        if (diagnostics.Count > 0)
        {
            await PersistRejectedAsync(operation, diagnostics[0].Code);
            return;
        }

        var normalized = AgentProfileDeterminism.NormalizeDraft(command.Draft);
        var digest = AgentProfileDeterminism.ComputeDraftDigest(normalized);
        var next = State.Clone();
        next.Operations.Add(operation.Clone());
        if (digest.Equals(State.DraftSha256))
        {
            next.LastMutation = Outcome(
                operation,
                AgentProfileMutationStatus.NoChange,
                "DRAFT_UNCHANGED",
                NextVersion(),
                next.DraftRevision,
                next.PublishedRevision);
        }
        else
        {
            next.Draft = normalized;
            next.DraftRevision = checked(State.DraftRevision + 1);
            next.DraftSha256 = digest;
            next.LastMutation = Outcome(
                operation,
                AgentProfileMutationStatus.Succeeded,
                "DRAFT_UPDATED",
                NextVersion(),
                next.DraftRevision,
                next.PublishedRevision);
        }
        await PersistAsync(next, "draft-updated");
    }

    [EventHandler]
    public async Task HandlePublishAsync(PublishAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity);
        var operation = CanonicalOperation(command.Operation, command);
        if (TryHandleReplay(operation))
        {
            if (State.Published is not null)
                await SendPublishedSummaryAsync(State.Published, operation.OperationId);
            return;
        }
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(operation, "AUTHORITY_VERSION_CONFLICT");
            return;
        }
        if (!command.SourceDraftSha256.Equals(State.DraftSha256))
        {
            await PersistRejectedAsync(operation, "DRAFT_SOURCE_MISMATCH");
            return;
        }
        if (command.Snapshot is null ||
            !AgentProfileDeterminism.VerifyPublishedSnapshot(command.Snapshot, State.Draft) ||
            !command.Snapshot.Identity.Equals(State.Identity) ||
            !command.Snapshot.DraftSha256.Equals(State.DraftSha256) ||
            command.Snapshot.DraftRevision != State.DraftRevision)
        {
            await PersistRejectedAsync(operation, "PUBLISHED_SNAPSHOT_INVALID");
            return;
        }

        var expectedRevision = checked(State.PublishedRevision + 1);
        if (command.Snapshot.PublishedRevision != expectedRevision)
        {
            await PersistRejectedAsync(operation, "PUBLISHED_REVISION_INVALID");
            return;
        }

        var next = State.Clone();
        next.Published = command.Snapshot.Clone();
        next.PublishedRevision = command.Snapshot.PublishedRevision;
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(
            operation,
            AgentProfileMutationStatus.Succeeded,
            "PROFILE_PUBLISHED",
            NextVersion(),
            next.DraftRevision,
            next.PublishedRevision);
        await PersistAsync(next, "published");
        await SendPublishedSummaryAsync(next.Published, operation.OperationId);
    }

    protected override AgentProfileState TransitionState(AgentProfileState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<AgentProfileStateChangedEvent>(static (_, changed) => changed.State.Clone())
            .OrCurrent();

    private Task PersistAsync(AgentProfileState state, string changeKind) =>
        PersistDomainEventAsync(new AgentProfileStateChangedEvent
        {
            State = state.Clone(),
            ChangeKind = changeKind,
        });

    private async Task PersistRejectedAsync(AgentProfileOperationFact operation, string code)
    {
        var next = State.Clone();
        next.Operations.Add(operation.Clone());
        next.LastMutation = Outcome(
            operation,
            AgentProfileMutationStatus.Rejected,
            code,
            NextVersion(),
            next.DraftRevision,
            next.PublishedRevision);
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
        State.Operations.FirstOrDefault(x =>
            string.Equals(x.OperationId, operationId, StringComparison.Ordinal));

    private Task SendInitializedAsync(
        AgentProfileIdentity identity,
        AgentProfileOperationFact operation,
        long sourceAuthorityStateVersion) =>
        SendToAsync(AgentProfileActorIds.Namespace(identity.Owner), new AgentProfileInitialized
        {
            Identity = identity.Clone(),
            Operation = operation.Clone(),
            SourceProfileActorId = Id,
            SourceAuthorityStateVersion = sourceAuthorityStateVersion,
            ProvisioningOperationId = operation.OperationId,
        });

    private Task SendInitializationFailedAsync(
        AgentProfileIdentity identity,
        AgentProfileOperationFact operation,
        string failureCode) =>
        SendToAsync(AgentProfileActorIds.Namespace(identity.Owner), new AgentProfileInitializationFailed
        {
            Identity = identity.Clone(),
            Operation = operation.Clone(),
            SourceProfileActorId = Id,
            SourceAuthorityStateVersion = CurrentVersion(),
            ProvisioningOperationId = operation.OperationId,
            FailureCode = failureCode,
        });

    private Task SendPublishedSummaryAsync(AgentProfilePublishedSnapshot snapshot, string sourceOperationId) =>
        SendToAsync(State.NamespaceActorId, new ObserveAgentProfilePublishedCommand
        {
            Identity = snapshot.Identity.Clone(),
            DisplayName = snapshot.DisplayName,
            Purpose = snapshot.Purpose,
            PublishedRevision = snapshot.PublishedRevision,
            SnapshotSha256 = snapshot.SnapshotSha256,
            SourceProfileActorId = Id,
            SourceAuthorityStateVersion = CurrentVersion(),
            SourceOperationId = sourceOperationId,
        });

    private void EnsureIdentity(AgentProfileIdentity identity)
    {
        ValidateIdentity(identity);
        EnsureActorAddress(identity.ProfileId);
        if (State.Identity is null || !State.Identity.Equals(identity))
            throw new InvalidOperationException("Agent Profile identity does not match the authority state.");
    }

    private static void ValidateIdentity(AgentProfileIdentity? identity)
    {
        if (identity?.Owner is null || identity.Owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.None ||
            string.IsNullOrWhiteSpace(identity.ProfileId) ||
            AgentProfilePolicies.ValidateProfileSlug(identity.ProfileSlug).Count > 0)
        {
            throw new InvalidOperationException("A valid Agent Profile identity is required.");
        }
        _ = AgentProfileActorIds.Namespace(identity.Owner);
    }

    private static void ValidateOperation(AgentProfileOperationFact? operation)
    {
        if (operation is null || string.IsNullOrWhiteSpace(operation.OperationId) ||
            string.IsNullOrWhiteSpace(operation.CommandId) ||
            string.IsNullOrWhiteSpace(operation.CorrelationId) || operation.InputSha256.Length != 32)
        {
            throw new InvalidOperationException("A complete Agent Profile operation fact is required.");
        }
    }

    private static void EnsureOperationReplay(
        AgentProfileOperationFact existing,
        AgentProfileOperationFact incoming)
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

    private void EnsureActorAddress(string profileId)
    {
        if (!string.Equals(Id, AgentProfileActorIds.Profile(profileId), StringComparison.Ordinal))
            throw new InvalidOperationException("Agent Profile authority address does not match its typed identity.");
    }

    private void EnsurePublisher(string expectedPublisherActorId)
    {
        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId ?? string.Empty;
        if (!string.Equals(publisherActorId, expectedPublisherActorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent Profile command publisher does not match namespace authority.");
    }

    private long CurrentVersion() => EventSourcing?.CurrentVersion ?? 0;

    private long NextVersion() => checked(CurrentVersion() + 1);

    private static AgentProfileMutationOutcome Outcome(
        AgentProfileOperationFact operation,
        AgentProfileMutationStatus status,
        string code,
        long authorityVersion,
        long draftRevision,
        long publishedRevision) => new()
    {
        Operation = operation.Clone(),
        Status = status,
        Code = code,
        AuthorityStateVersion = authorityVersion,
        DraftRevision = draftRevision,
        PublishedRevision = publishedRevision,
        RecordedAt = operation.RequestedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };
}
