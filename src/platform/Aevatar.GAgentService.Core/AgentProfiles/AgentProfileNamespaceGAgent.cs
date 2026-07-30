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

[GAgent("gagent.agent-profile-namespace")]
public sealed class AgentProfileNamespaceGAgent : GAgentBase<AgentProfileNamespaceState>, IProjectedActor
{
    public const string DurableProjectionKind = "agent-profile-catalog";

    public static string ProjectionKind => DurableProjectionKind;

    [EventHandler]
    public async Task HandleCreateAsync(CreateAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        ValidateOperation(command.Operation);
        if (AgentProfilePolicies.ValidateProfileSlug(command.ProfileSlug).Count > 0 ||
            string.IsNullOrWhiteSpace(command.ProfileId) || string.IsNullOrWhiteSpace(command.ProfileActorId))
        {
            throw new InvalidOperationException("A valid Agent Profile target is required.");
        }
        EnsureOwner(command.Owner);

        if (FindOperation(command.Operation.OperationId) is { } existing)
        {
            EnsureOperationReplay(existing, command.Operation);
            await SendInitializeAsync(command);
            return;
        }

        if (State.Profiles.Any(x => string.Equals(x.ProfileSlug, command.ProfileSlug, StringComparison.Ordinal)))
        {
            await PersistRejectedAsync(command.Operation, "PROFILE_SLUG_TAKEN");
            return;
        }

        var next = State.Clone();
        next.Owner = command.Owner.Clone();
        next.Profiles.Add(new AgentProfileCatalogEntry
        {
            ProfileId = command.ProfileId.Trim(),
            ProfileSlug = command.ProfileSlug,
            ProfileActorId = command.ProfileActorId.Trim(),
            Status = AgentProfileProvisioningStatus.Provisioning,
        });
        next.Operations.Add(command.Operation.Clone());
        next.LastMutation = Outcome(command.Operation, AgentProfileMutationStatus.Succeeded,
            "PROFILE_PROVISIONING_STARTED", NextVersion());
        await PersistAsync(next, "provisioning-started");
        await SendInitializeAsync(command);
    }

    [EventHandler]
    public async Task HandleInitializedAsync(AgentProfileInitialized initialized)
    {
        ArgumentNullException.ThrowIfNull(initialized);
        ValidateIdentity(initialized.Identity);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, initialized.Identity.ProfileId, StringComparison.Ordinal));
        if (entry is null || !AgentProfileDeterminism.SameOwner(State.Owner, initialized.Identity.Owner) ||
            !string.Equals(entry.ProfileSlug, initialized.Identity.ProfileSlug, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent Profile initialization does not match namespace authority.");
        }
        if (entry.Status == AgentProfileProvisioningStatus.Active)
            return;

        var next = State.Clone();
        next.Profiles.First(x => x.ProfileId == entry.ProfileId).Status = AgentProfileProvisioningStatus.Active;
        next.LastMutation = Outcome(initialized.Operation, AgentProfileMutationStatus.Succeeded,
            "PROFILE_ACTIVE", NextVersion());
        await PersistAsync(next, "provisioning-completed");
    }

    [EventHandler]
    public async Task HandleObservePublishedAsync(ObserveAgentProfilePublishedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateIdentity(command.Identity);
        var entry = State.Profiles.FirstOrDefault(x =>
            string.Equals(x.ProfileId, command.Identity.ProfileId, StringComparison.Ordinal));
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
        ValidateOperation(command.Operation);
        EnsureOwner(command.Owner);
        if (TryHandleReplay(command.Operation))
            return;
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(command.Operation, "AUTHORITY_VERSION_CONFLICT");
            return;
        }
        if (!AgentProfilePolicies.IsSupportedAgentKind(command.AgentKind) ||
            command.CohortBasisPoints is < 0 or > AgentProfilePolicies.FullCohortBasisPoints)
        {
            await PersistRejectedAsync(command.Operation, "BINDING_INVALID");
            return;
        }
        var profile = State.Profiles.FirstOrDefault(x => x.ProfileId == command.ProfileId);
        if (profile is null || profile.Status != AgentProfileProvisioningStatus.Active)
        {
            await PersistRejectedAsync(command.Operation, "PROFILE_NOT_FOUND");
            return;
        }
        if (profile.PublishedRevision <= 0 || profile.SnapshotSha256.Length != 32)
        {
            await PersistRejectedAsync(command.Operation, "PROFILE_NOT_PUBLISHED");
            return;
        }

        var next = State.Clone();
        var existingBinding = next.DefaultBindings.FirstOrDefault(x => x.AgentKind == command.AgentKind);
        if (existingBinding is not null)
            next.DefaultBindings.Remove(existingBinding);
        next.DefaultBindings.Add(new AgentProfileDefaultBinding
        {
            AgentKind = command.AgentKind,
            ProfileId = command.ProfileId,
            Enabled = command.Enabled,
            CohortBasisPoints = command.CohortBasisPoints,
        });
        next.Operations.Add(command.Operation.Clone());
        next.LastMutation = Outcome(command.Operation, AgentProfileMutationStatus.Succeeded,
            "DEFAULT_BINDING_SET", NextVersion());
        await PersistAsync(next, "default-binding-set");
    }

    [EventHandler]
    public async Task HandleClearDefaultBindingAsync(ClearAgentProfileDefaultBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateOwner(command.Owner);
        ValidateOperation(command.Operation);
        EnsureOwner(command.Owner);
        if (TryHandleReplay(command.Operation))
            return;
        if (command.ExpectedAuthorityStateVersion != CurrentVersion())
        {
            await PersistRejectedAsync(command.Operation, "AUTHORITY_VERSION_CONFLICT");
            return;
        }
        if (!AgentProfilePolicies.IsSupportedAgentKind(command.AgentKind))
        {
            await PersistRejectedAsync(command.Operation, "BINDING_INVALID");
            return;
        }

        var next = State.Clone();
        var existing = next.DefaultBindings.FirstOrDefault(x => x.AgentKind == command.AgentKind);
        if (existing is not null)
            next.DefaultBindings.Remove(existing);
        next.Operations.Add(command.Operation.Clone());
        next.LastMutation = Outcome(command.Operation,
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

    private Task SendInitializeAsync(CreateAgentProfileCommand command) =>
        SendToAsync(command.ProfileActorId, new InitializeAgentProfileCommand
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = command.ProfileId,
                Owner = command.Owner.Clone(),
                ProfileSlug = command.ProfileSlug,
            },
            NamespaceActorId = Id,
            Operation = command.Operation.Clone(),
        });

    private async Task PersistRejectedAsync(AgentProfileOperationFact operation, string code)
    {
        var next = State.Clone();
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
        if (identity?.Owner is null || string.IsNullOrWhiteSpace(identity.ProfileId) ||
            AgentProfilePolicies.ValidateProfileSlug(identity.ProfileSlug).Count > 0)
            throw new InvalidOperationException("A valid Agent Profile identity is required.");
    }

    private static void ValidateOperation(AgentProfileOperationFact? operation)
    {
        if (operation is null || string.IsNullOrWhiteSpace(operation.OperationId) ||
            operation.InputSha256.Length != 32)
            throw new InvalidOperationException("A complete Agent Profile operation fact is required.");
    }

    private static void EnsureOperationReplay(AgentProfileOperationFact existing, AgentProfileOperationFact incoming)
    {
        if (!existing.InputSha256.Equals(incoming.InputSha256))
            throw new InvalidOperationException("Agent Profile operation payload drift is not allowed.");
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
