using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.AgentProfiles;

[GAgent("gagent.service.agent-profile")]
public sealed class AgentProfileGAgent : GAgentBase<AgentProfileState>
{
    public AgentProfileGAgent() => InitializeId();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var namespaceActorId = AgentProfileActorInvariants.RequireActorId(
            command.NamespaceActorId,
            "namespace_actor_id");

        AgentProfileIdentity identity;
        try
        {
            identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);
        }
        catch (Exception exception) when (
            exception is AgentProfileContractValidationException or ArgumentNullException)
        {
            throw AgentProfileActorInvariants.Error(
                "INVALID_PROFILE_INITIALIZATION_IDENTITY",
                "Initialization requires a canonical safe Profile identity.");
        }

        AgentProfileContent content;
        try
        {
            content = AgentProfileDeterminism.NormalizeContent(command.InitialContent);
        }
        catch (AgentProfileContractValidationException exception)
        {
            var rejectedContentSha256 = AgentProfileDeterminism.Sha256(command.InitialContent);
            var existingRejection = FindOperation(operation.OperationId);
            if (existingRejection is not null)
            {
                EnsureInitializationRejectionReplay(
                    existingRejection,
                    operation,
                    identity,
                    namespaceActorId,
                    rejectedContentSha256);
                await SendInitializationRejectedAsync(existingRejection, operation);
                return;
            }

            await PersistInitializationRejectionAsync(
                namespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.FirstDiagnostic(exception),
                rejectedContentSha256);
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content);
        var rejectedContentFingerprint = AgentProfileDeterminism.Sha256(content);
        var existingOperation = FindOperation(operation.OperationId);
        if (existingOperation is not null)
        {
            EnsureInitializationReplay(
                existingOperation,
                operation,
                identity,
                namespaceActorId,
                expectedInput,
                rejectedContentFingerprint);
            if (existingOperation.InitializationRejection is not null)
            {
                await SendInitializationRejectedAsync(existingOperation, operation);
                return;
            }
            if (existingOperation.InitializationContinuation is not null)
            {
                await SendInitializedAsync(namespaceActorId, operation);
                return;
            }

            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "The initialization operation has no replayable typed outcome.");
        }

        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(content))
        {
            await PersistInitializationRejectionAsync(
                namespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.MultipleDefaultSkills(),
                rejectedContentFingerprint);
            return;
        }

        if (State.Identity is not null)
        {
            await PersistInitializationRejectionAsync(
                State.NamespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.IdentityConflict(),
                rejectedContentFingerprint);
            return;
        }

        if (!AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
        {
            await PersistInitializationRejectionAsync(
                namespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.InputDigestMismatch(),
                rejectedContentFingerprint);
            return;
        }

        var draftSha256 = AgentProfileDeterminism.ComputeDraftSha256(content);
        await PersistDomainEventAsync(new AgentProfileInitializedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            InitialContent = content.Clone(),
            DraftRevision = 1,
            DraftSha256 = draftSha256,
            NamespaceActorId = namespaceActorId,
            ProfileActorId = Id,
        });
        await SendInitializedAsync(namespaceActorId, operation);
    }

    [EventHandler]
    public async Task HandleUpdateDraftAsync(UpdateAgentProfileDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var identity = await NormalizeMutationIdentityAsync(operation, command.Identity);
        if (identity is null)
            return;

        AgentProfileContent content;
        try
        {
            content = AgentProfileDeterminism.NormalizeContent(command.Content);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistUncanonicalizedRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
            identity,
            content);
        if (!await PrepareMutationAsync(operation, identity, expectedInput))
            return;
        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(content))
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.MultipleDefaultSkills());
            return;
        }
        if (!await EnsureExpectedVersionAsync(operation, command.ExpectedAuthorityStateVersion))
            return;

        var draftSha256 = AgentProfileDeterminism.ComputeDraftSha256(content);
        if (State.Draft.Equals(content) &&
            AgentProfileActorInvariants.DigestEquals(State.DraftSha256, draftSha256))
        {
            await PersistNoChangeAsync(operation);
            return;
        }

        var outcome = AgentProfileActorInvariants.Outcome(
            State,
            operation,
            AgentProfileMutationStatus.Applied,
            draftRevision: checked(State.DraftRevision + 1),
            draftSha256: draftSha256);
        await PersistDomainEventAsync(new AgentProfileDraftUpdatedEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity.Clone(),
            Content = content.Clone(),
            DraftRevision = outcome.DraftRevision,
            DraftSha256 = draftSha256,
            Outcome = outcome,
        });
    }

    [EventHandler]
    public async Task HandleUpsertSkillBindingAsync(UpsertAgentProfileSkillBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var identity = await NormalizeMutationIdentityAsync(operation, command.Identity);
        if (identity is null)
            return;

        AgentProfileSkillBinding binding;
        try
        {
            binding = AgentProfileDeterminism.NormalizeSkillBinding(command.Binding);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistUncanonicalizedRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
            identity,
            binding);
        if (!await PrepareMutationAsync(operation, identity, expectedInput))
            return;
        if (!await EnsureExpectedVersionAsync(operation, command.ExpectedAuthorityStateVersion))
            return;

        var candidate = State.Draft.Clone();
        var existingIndex = FindBindingIndex(candidate, binding.BindingId);
        if (existingIndex >= 0)
            candidate.SkillBindings.RemoveAt(existingIndex);
        candidate.SkillBindings.Add(binding.Clone());
        candidate = AgentProfileDeterminism.NormalizeContent(candidate);
        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(candidate))
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.MultipleDefaultSkills());
            return;
        }

        var draftSha256 = AgentProfileDeterminism.ComputeDraftSha256(candidate);
        if (State.Draft.Equals(candidate))
        {
            await PersistNoChangeAsync(operation);
            return;
        }

        var outcome = AgentProfileActorInvariants.Outcome(
            State,
            operation,
            AgentProfileMutationStatus.Applied,
            draftRevision: checked(State.DraftRevision + 1),
            draftSha256: draftSha256);
        await PersistDomainEventAsync(new AgentProfileSkillBindingUpsertedEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity.Clone(),
            Binding = binding.Clone(),
            Content = candidate.Clone(),
            DraftRevision = outcome.DraftRevision,
            DraftSha256 = draftSha256,
            Outcome = outcome,
        });
    }

    [EventHandler]
    public async Task HandleRemoveSkillBindingAsync(RemoveAgentProfileSkillBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var identity = await NormalizeMutationIdentityAsync(operation, command.Identity);
        if (identity is null)
            return;

        ByteString expectedInput;
        try
        {
            expectedInput = AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                identity,
                command.BindingId);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistUncanonicalizedRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        if (!await PrepareMutationAsync(operation, identity, expectedInput))
            return;
        if (!await EnsureExpectedVersionAsync(operation, command.ExpectedAuthorityStateVersion))
            return;

        var bindingId = command.BindingId.Normalize(System.Text.NormalizationForm.FormC);
        var existingIndex = FindBindingIndex(State.Draft, bindingId);
        if (existingIndex < 0)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.MissingBinding(bindingId));
            return;
        }

        var candidate = State.Draft.Clone();
        candidate.SkillBindings.RemoveAt(existingIndex);
        candidate = AgentProfileDeterminism.NormalizeContent(candidate);
        var draftSha256 = AgentProfileDeterminism.ComputeDraftSha256(candidate);
        var outcome = AgentProfileActorInvariants.Outcome(
            State,
            operation,
            AgentProfileMutationStatus.Applied,
            draftRevision: checked(State.DraftRevision + 1),
            draftSha256: draftSha256);
        await PersistDomainEventAsync(new AgentProfileSkillBindingRemovedEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity.Clone(),
            BindingId = bindingId,
            Content = candidate.Clone(),
            DraftRevision = outcome.DraftRevision,
            DraftSha256 = draftSha256,
            Outcome = outcome,
        });
    }

    [EventHandler]
    public async Task HandlePublishAsync(PublishAgentProfileCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        var identity = await NormalizeMutationIdentityAsync(operation, command.Identity);
        if (identity is null)
            return;

        AgentProfilePublishedSnapshot snapshot;
        try
        {
            snapshot = AgentProfileDeterminism.NormalizePublishedSnapshot(command.Snapshot);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistUncanonicalizedRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
            identity,
            snapshot);
        if (!await PrepareMutationAsync(
                operation,
                identity,
                expectedInput,
                existing => existing.PublishedSummary is null
                    ? Task.CompletedTask
                    : SendPublishedSummaryAsync(operation, existing.PublishedSummary)))
            return;
        if (!AgentProfileActorInvariants.SameIdentity(snapshot.Identity, State.Identity))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.IdentityConflict());
            return;
        }
        var hardLimitDiagnostic = AgentProfilePolicies
            .ValidatePublishedSnapshotHardLimits(snapshot)
            .FirstOrDefault();
        if (hardLimitDiagnostic is not null)
        {
            await PersistNewRejectionAsync(operation, hardLimitDiagnostic);
            return;
        }
        if (!await EnsureExpectedVersionAsync(operation, command.ExpectedAuthorityStateVersion))
            return;
        if (command.ExpectedDraftRevision != State.DraftRevision ||
            !AgentProfileActorInvariants.DigestEquals(command.ExpectedDraftSha256, State.DraftSha256) ||
            !AgentProfileActorInvariants.DigestEquals(snapshot.SourceDraftSha256, State.DraftSha256))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.PublishSourceChanged());
            return;
        }
        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(State.Draft))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.MultipleDefaultSkills());
            return;
        }

        var snapshotMismatch = AgentProfileActorInvariants.ValidateSnapshotMatchesDraft(snapshot, State.Draft);
        if (snapshotMismatch is not null)
        {
            await PersistNewRejectionAsync(operation, snapshotMismatch);
            return;
        }

        var expectedSnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        if (!AgentProfileActorInvariants.DigestEquals(snapshot.SnapshotSha256, expectedSnapshotSha256))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.SnapshotDigestMismatch());
            return;
        }
        if (snapshot.PublishedRevision != 0)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.Diagnostic(
                    "INVALID_PUBLISHED_REVISION",
                    "Publish input must not assign the authoritative revision.",
                    "snapshot.published_revision"));
            return;
        }

        if (State.Published is not null &&
            AgentProfileActorInvariants.DigestEquals(
                snapshot.SourceDraftSha256,
                State.Published.SourceDraftSha256) &&
            AgentProfileActorInvariants.DigestEquals(
                snapshot.SnapshotSha256,
                State.Published.SnapshotSha256))
        {
            var summary = AgentProfileActorInvariants.Summary(State.Published);
            var noChangeOutcome = AgentProfileActorInvariants.Outcome(
                State,
                operation,
                AgentProfileMutationStatus.NoChange);
            await PersistDomainEventAsync(new AgentProfilePublishNoChangeEvent
            {
                Operation = operation.Clone(),
                Identity = State.Identity.Clone(),
                Summary = summary.Clone(),
                Outcome = noChangeOutcome,
            });
            await SendPublishedSummaryAsync(operation, summary);
            return;
        }

        var published = snapshot.Clone();
        published.PublishedRevision = checked(State.PublishedRevision + 1);
        var outcome = AgentProfileActorInvariants.Outcome(
            State,
            operation,
            AgentProfileMutationStatus.Applied,
            publishedRevision: published.PublishedRevision,
            publishedSnapshotSha256: published.SnapshotSha256);
        await PersistDomainEventAsync(new AgentProfilePublishedEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity.Clone(),
            Snapshot = published.Clone(),
            Outcome = outcome,
        });
        await SendPublishedSummaryAsync(operation, AgentProfileActorInvariants.Summary(published));
    }

    protected override AgentProfileState TransitionState(AgentProfileState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentProfileInitializedEvent>(ApplyInitialized)
            .On<AgentProfileInitializationRejectedEvent>(ApplyInitializationRejected)
            .On<AgentProfileDraftUpdatedEvent>(ApplyDraftUpdated)
            .On<AgentProfileSkillBindingUpsertedEvent>(ApplyBindingUpserted)
            .On<AgentProfileSkillBindingRemovedEvent>(ApplyBindingRemoved)
            .On<AgentProfilePublishedEvent>(ApplyPublished)
            .On<AgentProfilePublishNoChangeEvent>(ApplyPublishNoChange)
            .On<AgentProfileMutationNoChangeEvent>(ApplyMutationNoChange)
            .On<AgentProfileMutationRejectedEvent>(ApplyMutationRejected)
            .OrCurrent();

    private async Task<AgentProfileIdentity?> NormalizeMutationIdentityAsync(
        AgentProfileOperationFact operation,
        AgentProfileIdentity? candidate)
    {
        if (State.Identity is null)
        {
            throw AgentProfileActorInvariants.Error(
                "PROFILE_NOT_INITIALIZED",
                "The Profile authority has not been initialized.");
        }

        AgentProfileIdentity identity;
        try
        {
            identity = AgentProfileDeterminism.NormalizeIdentity(candidate!);
        }
        catch (Exception exception) when (
            exception is AgentProfileContractValidationException or ArgumentNullException)
        {
            await PersistUncanonicalizedRejectionAsync(
                operation,
                AgentProfileActorInvariants.IdentityConflict());
            return null;
        }

        return identity;
    }

    private async Task<bool> PrepareMutationAsync(
        AgentProfileOperationFact operation,
        AgentProfileIdentity identity,
        ByteString expectedInput,
        Func<AgentProfileOperationState, Task>? replay = null)
    {
        var existing = FindOperation(operation.OperationId);
        if (existing is not null)
        {
            EnsureReplay(existing, operation, expectedInput);
            if (replay is not null)
                await replay(existing);
            return false;
        }

        if (!AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.InputDigestMismatch());
            return false;
        }

        if (!AgentProfileActorInvariants.SameIdentity(State.Identity, identity))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.IdentityConflict());
            return false;
        }

        return true;
    }

    private async Task<bool> EnsureExpectedVersionAsync(
        AgentProfileOperationFact operation,
        long expectedVersion)
    {
        if (expectedVersion == CurrentStateVersion())
            return true;
        await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.VersionConflict());
        return false;
    }

    private Task PersistNoChangeAsync(AgentProfileOperationFact operation) =>
        PersistDomainEventAsync(new AgentProfileMutationNoChangeEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity.Clone(),
            Outcome = AgentProfileActorInvariants.Outcome(
                State,
                operation,
                AgentProfileMutationStatus.NoChange),
        });

    private Task PersistNewRejectionAsync(
        AgentProfileOperationFact operation,
        AgentProfileSafeDiagnostic diagnostic)
    {
        var existing = FindOperation(operation.OperationId);
        if (existing is not null)
        {
            if (AgentProfileActorInvariants.SameInput(existing.Operation, operation))
                return Task.CompletedTask;
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An operation id cannot be reused with a different normalized input.");
        }

        return PersistDomainEventAsync(new AgentProfileMutationRejectedEvent
        {
            Operation = operation.Clone(),
            Identity = State.Identity?.Clone() ?? new AgentProfileIdentity(),
            Outcome = AgentProfileActorInvariants.Outcome(
                State,
                operation,
                AgentProfileMutationStatus.Rejected,
                diagnostic),
        });
    }

    private Task PersistUncanonicalizedRejectionAsync(
        AgentProfileOperationFact operation,
        AgentProfileSafeDiagnostic diagnostic)
    {
        if (FindOperation(operation.OperationId) is not null)
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An operation id cannot be reused with input that cannot be canonicalized.");
        }

        return PersistNewRejectionAsync(operation, diagnostic);
    }

    private Task SendInitializedAsync(
        string namespaceActorId,
        AgentProfileOperationFact operation)
    {
        var stored = FindOperation(operation.OperationId)?.InitializationContinuation
            ?? throw AgentProfileActorInvariants.Error(
                "MISSING_INITIALIZATION_CONTINUATION",
                "The committed initialization continuation is unavailable.");
        var continuation = stored.Clone();
        continuation.Operation = operation.Clone();
        return SendToAsync(
            namespaceActorId,
            continuation,
            CancellationToken.None);
    }

    private async Task PersistInitializationRejectionAsync(
        string namespaceActorId,
        AgentProfileOperationFact operation,
        AgentProfileIdentity identity,
        AgentProfileSafeDiagnostic diagnostic,
        ByteString rejectedContentSha256)
    {
        await PersistDomainEventAsync(new AgentProfileInitializationRejectedEvent
        {
            Operation = operation.Clone(),
            Identity = identity.Clone(),
            NamespaceActorId = namespaceActorId,
            ProfileActorId = Id,
            Diagnostic = diagnostic.Clone(),
            RejectedContentSha256 = rejectedContentSha256,
        });
        await SendInitializationRejectedAsync(
            FindOperation(operation.OperationId)
                ?? throw AgentProfileActorInvariants.Error(
                    "MISSING_INITIALIZATION_REJECTION",
                    "The committed initialization rejection is unavailable."),
            operation);
    }

    private Task SendInitializationRejectedAsync(
        AgentProfileOperationState operationState,
        AgentProfileOperationFact operation)
    {
        var rejection = operationState.InitializationRejection
            ?? throw AgentProfileActorInvariants.Error(
                "MISSING_INITIALIZATION_REJECTION",
                "The committed initialization rejection is unavailable.");
        var continuation = rejection.Continuation.Clone();
        continuation.Operation = operation.Clone();
        return SendToAsync(
            rejection.NamespaceActorId,
            continuation,
            CancellationToken.None);
    }

    private Task SendPublishedSummaryAsync(
        AgentProfileOperationFact operation,
        AgentProfilePublishedSummary summary) =>
        SendToAsync(
            State.NamespaceActorId,
            new ObserveAgentProfilePublishedSummaryCommand
            {
                Operation = operation.Clone(),
                Identity = State.Identity.Clone(),
                Summary = summary.Clone(),
            },
            CancellationToken.None);

    private AgentProfileOperationState? FindOperation(string operationId) =>
        State.Operations.FirstOrDefault(candidate =>
            string.Equals(candidate.Operation?.OperationId, operationId, StringComparison.Ordinal));

    private long CurrentStateVersion() =>
        (EventSourcing ?? throw new InvalidOperationException(
            "Event sourcing must be configured before mutating a Profile."))
        .CurrentVersion;

    private static void EnsureReplay(
        AgentProfileOperationState existing,
        AgentProfileOperationFact candidate,
        ByteString expectedInput)
    {
        if (!AgentProfileActorInvariants.SameInput(existing.Operation, candidate) ||
            !AgentProfileActorInvariants.DigestEquals(candidate.InputSha256, expectedInput))
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An operation id cannot be reused with a different normalized input.");
        }
    }

    private void EnsureInitializationReplay(
        AgentProfileOperationState existing,
        AgentProfileOperationFact candidate,
        AgentProfileIdentity identity,
        string namespaceActorId,
        ByteString expectedInput,
        ByteString rejectedContentSha256)
    {
        var storedIdentity = existing.InitializationContinuation?.Identity ??
            existing.InitializationRejection?.Continuation?.Identity;
        var storedProfileActorId = existing.InitializationContinuation?.ProfileActorId ??
            existing.InitializationRejection?.Continuation?.ProfileActorId;
        var storedNamespaceActorId = existing.InitializationRejection?.NamespaceActorId ??
            State.NamespaceActorId;
        if (!AgentProfileActorInvariants.SameInput(existing.Operation, candidate) ||
            !AgentProfileActorInvariants.DigestEquals(candidate.InputSha256, expectedInput) ||
            !AgentProfileActorInvariants.SameIdentity(storedIdentity, identity) ||
            !string.Equals(storedProfileActorId, Id, StringComparison.Ordinal) ||
            !string.Equals(storedNamespaceActorId, namespaceActorId, StringComparison.Ordinal) ||
            existing.InitializationRejection is not null &&
            !AgentProfileActorInvariants.DigestEquals(
                existing.InitializationRejection.RejectedContentSha256,
                rejectedContentSha256))
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An initialization operation cannot change its normalized input or Actor relation.");
        }
    }

    private void EnsureInitializationRejectionReplay(
        AgentProfileOperationState existing,
        AgentProfileOperationFact candidate,
        AgentProfileIdentity identity,
        string namespaceActorId,
        ByteString rejectedContentSha256)
    {
        var rejection = existing.InitializationRejection;
        if (rejection is null ||
            !AgentProfileActorInvariants.SameInput(existing.Operation, candidate) ||
            !AgentProfileActorInvariants.SameIdentity(rejection.Continuation?.Identity, identity) ||
            !string.Equals(rejection.Continuation?.ProfileActorId, Id, StringComparison.Ordinal) ||
            !string.Equals(rejection.NamespaceActorId, namespaceActorId, StringComparison.Ordinal) ||
            !AgentProfileActorInvariants.DigestEquals(
                rejection.RejectedContentSha256,
                rejectedContentSha256))
        {
            throw AgentProfileActorInvariants.Error(
                "IDEMPOTENCY_PAYLOAD_CONFLICT",
                "An initialization rejection cannot change its typed input or Actor relation.");
        }
    }

    private static int FindBindingIndex(AgentProfileContent content, string bindingId)
    {
        for (var index = 0; index < content.SkillBindings.Count; index++)
        {
            if (string.Equals(content.SkillBindings[index].BindingId, bindingId, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static AgentProfileState ApplyInitialized(
        AgentProfileState state,
        AgentProfileInitializedEvent evt)
    {
        var next = new AgentProfileState
        {
            Identity = evt.Identity.Clone(),
            NamespaceActorId = evt.NamespaceActorId,
            Draft = evt.InitialContent.Clone(),
            DraftRevision = evt.DraftRevision,
            DraftSha256 = evt.DraftSha256,
        };
        next.Operations.Add(state.Operations.Select(static operation => operation.Clone()));
        next.Operations.Add(new AgentProfileOperationState
        {
            Operation = evt.Operation.Clone(),
            InitializationContinuation = new AgentProfileInitializedContinuation
            {
                Operation = evt.Operation.Clone(),
                Identity = evt.Identity.Clone(),
                ProfileActorId = evt.ProfileActorId,
                DraftRevision = evt.DraftRevision,
                DraftSha256 = evt.DraftSha256,
            },
        });
        return next;
    }

    private static AgentProfileState ApplyInitializationRejected(
        AgentProfileState state,
        AgentProfileInitializationRejectedEvent evt)
    {
        var next = state.Clone();
        next.Operations.Add(new AgentProfileOperationState
        {
            Operation = evt.Operation.Clone(),
            InitializationRejection = new AgentProfileInitializationRejectionState
            {
                NamespaceActorId = evt.NamespaceActorId,
                Continuation = new AgentProfileInitializationRejectedContinuation
                {
                    Operation = evt.Operation.Clone(),
                    Identity = evt.Identity.Clone(),
                    ProfileActorId = evt.ProfileActorId,
                    Diagnostic = evt.Diagnostic.Clone(),
                },
                RejectedContentSha256 = evt.RejectedContentSha256,
            },
        });
        return next;
    }

    private static AgentProfileState ApplyDraftUpdated(
        AgentProfileState state,
        AgentProfileDraftUpdatedEvent evt) =>
        ApplyDraftMutation(state, evt.Content, evt.DraftRevision, evt.DraftSha256, evt.Outcome);

    private static AgentProfileState ApplyBindingUpserted(
        AgentProfileState state,
        AgentProfileSkillBindingUpsertedEvent evt) =>
        ApplyDraftMutation(state, evt.Content, evt.DraftRevision, evt.DraftSha256, evt.Outcome);

    private static AgentProfileState ApplyBindingRemoved(
        AgentProfileState state,
        AgentProfileSkillBindingRemovedEvent evt) =>
        ApplyDraftMutation(state, evt.Content, evt.DraftRevision, evt.DraftSha256, evt.Outcome);

    private static AgentProfileState ApplyDraftMutation(
        AgentProfileState state,
        AgentProfileContent content,
        long draftRevision,
        ByteString draftSha256,
        AgentProfileMutationOutcome outcome)
    {
        var next = state.Clone();
        next.Draft = content.Clone();
        next.DraftRevision = draftRevision;
        next.DraftSha256 = draftSha256;
        next.LastMutation = outcome.Clone();
        AddOperation(next, outcome, null);
        return next;
    }

    private static AgentProfileState ApplyPublished(
        AgentProfileState state,
        AgentProfilePublishedEvent evt)
    {
        var next = state.Clone();
        next.Published = evt.Snapshot.Clone();
        next.PublishedRevision = evt.Snapshot.PublishedRevision;
        next.LastMutation = evt.Outcome.Clone();
        AddOperation(next, evt.Outcome, AgentProfileActorInvariants.Summary(evt.Snapshot));
        return next;
    }

    private static AgentProfileState ApplyPublishNoChange(
        AgentProfileState state,
        AgentProfilePublishNoChangeEvent evt)
    {
        var next = state.Clone();
        next.LastMutation = evt.Outcome.Clone();
        AddOperation(next, evt.Outcome, evt.Summary);
        return next;
    }

    private static AgentProfileState ApplyMutationNoChange(
        AgentProfileState state,
        AgentProfileMutationNoChangeEvent evt)
    {
        var next = state.Clone();
        next.LastMutation = evt.Outcome.Clone();
        AddOperation(next, evt.Outcome, null);
        return next;
    }

    private static AgentProfileState ApplyMutationRejected(
        AgentProfileState state,
        AgentProfileMutationRejectedEvent evt)
    {
        var next = state.Clone();
        next.LastMutation = evt.Outcome.Clone();
        AddOperation(next, evt.Outcome, null);
        return next;
    }

    private static void AddOperation(
        AgentProfileState state,
        AgentProfileMutationOutcome outcome,
        AgentProfilePublishedSummary? summary)
    {
        var operation = new AgentProfileOperationState
        {
            Operation = outcome.Operation.Clone(),
            Outcome = outcome.Clone(),
        };
        if (summary is not null)
            operation.PublishedSummary = summary.Clone();
        state.Operations.Add(operation);
    }
}
