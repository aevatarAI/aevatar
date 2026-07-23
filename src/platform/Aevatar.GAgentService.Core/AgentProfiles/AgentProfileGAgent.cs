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
        AgentProfileContent content;
        try
        {
            identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);
            content = AgentProfileDeterminism.NormalizeContent(command.InitialContent);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await SendInitializationRejectedAsync(
                namespaceActorId,
                operation,
                command.Identity,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(content))
        {
            await SendInitializationRejectedAsync(
                namespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.MultipleDefaultSkills());
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content);
        if (State.Identity is not null)
        {
            var existingOperation = FindOperation(operation.OperationId);
            if (existingOperation is not null &&
                AgentProfileActorInvariants.SameInput(existingOperation.Operation, operation) &&
                AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput) &&
                AgentProfileActorInvariants.SameIdentity(State.Identity, identity) &&
                string.Equals(State.NamespaceActorId, namespaceActorId, StringComparison.Ordinal))
            {
                await SendInitializedAsync(namespaceActorId, operation);
                return;
            }

            var diagnostic = existingOperation is not null
                ? AgentProfileActorInvariants.Diagnostic(
                    "IDEMPOTENCY_PAYLOAD_CONFLICT",
                    "An initialization operation cannot change its normalized input.",
                    "operation.input_sha256")
                : AgentProfileActorInvariants.IdentityConflict();
            await SendInitializationRejectedAsync(
                State.NamespaceActorId,
                operation,
                identity,
                diagnostic);
            return;
        }

        if (!AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
        {
            await SendInitializationRejectedAsync(
                namespaceActorId,
                operation,
                identity,
                AgentProfileActorInvariants.InputDigestMismatch());
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
        });
        await SendInitializedAsync(namespaceActorId, operation);
    }

    [EventHandler]
    public async Task HandleUpdateDraftAsync(UpdateAgentProfileDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
        if (!await EnsureMutationIdentityAsync(operation, command.Identity))
            return;

        AgentProfileContent content;
        try
        {
            content = AgentProfileDeterminism.NormalizeContent(command.Content);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        if (!AgentProfileActorInvariants.HasAtMostOneDefaultBinding(content))
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.MultipleDefaultSkills());
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
            State.Identity,
            content);
        if (await HandleMutationReplayOrInputFailureAsync(operation, expectedInput))
            return;
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
        if (!await EnsureMutationIdentityAsync(operation, command.Identity))
            return;

        AgentProfileSkillBinding binding;
        try
        {
            binding = AgentProfileDeterminism.NormalizeSkillBinding(command.Binding);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
            State.Identity,
            binding);
        if (await HandleMutationReplayOrInputFailureAsync(operation, expectedInput))
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
        if (!await EnsureMutationIdentityAsync(operation, command.Identity))
            return;

        ByteString expectedInput;
        try
        {
            expectedInput = AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                State.Identity,
                command.BindingId);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        if (await HandleMutationReplayOrInputFailureAsync(operation, expectedInput))
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
        if (!await EnsureMutationIdentityAsync(operation, command.Identity))
            return;

        AgentProfilePublishedSnapshot snapshot;
        try
        {
            snapshot = AgentProfileDeterminism.NormalizePublishedSnapshot(command.Snapshot);
        }
        catch (AgentProfileContractValidationException exception)
        {
            await PersistNewRejectionAsync(
                operation,
                AgentProfileActorInvariants.FirstDiagnostic(exception));
            return;
        }

        var expectedInput = AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
            State.Identity,
            snapshot);
        var existingOperation = FindOperation(operation.OperationId);
        if (existingOperation is not null)
        {
            EnsureReplay(existingOperation, operation, expectedInput);
            if (existingOperation.PublishedSummary is not null)
            {
                await SendPublishedSummaryAsync(operation, existingOperation.PublishedSummary);
            }
            return;
        }

        if (!AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
        {
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.InputDigestMismatch());
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
            .On<AgentProfileDraftUpdatedEvent>(ApplyDraftUpdated)
            .On<AgentProfileSkillBindingUpsertedEvent>(ApplyBindingUpserted)
            .On<AgentProfileSkillBindingRemovedEvent>(ApplyBindingRemoved)
            .On<AgentProfilePublishedEvent>(ApplyPublished)
            .On<AgentProfilePublishNoChangeEvent>(ApplyPublishNoChange)
            .On<AgentProfileMutationNoChangeEvent>(ApplyMutationNoChange)
            .On<AgentProfileMutationRejectedEvent>(ApplyMutationRejected)
            .OrCurrent();

    private async Task<bool> EnsureMutationIdentityAsync(
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
            await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.IdentityConflict());
            return false;
        }

        if (AgentProfileActorInvariants.SameIdentity(State.Identity, identity))
            return true;

        await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.IdentityConflict());
        return false;
    }

    private async Task<bool> HandleMutationReplayOrInputFailureAsync(
        AgentProfileOperationFact operation,
        ByteString expectedInput)
    {
        var existing = FindOperation(operation.OperationId);
        if (existing is not null)
        {
            EnsureReplay(existing, operation, expectedInput);
            return true;
        }

        if (AgentProfileActorInvariants.DigestEquals(operation.InputSha256, expectedInput))
            return false;

        await PersistNewRejectionAsync(operation, AgentProfileActorInvariants.InputDigestMismatch());
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

    private Task SendInitializedAsync(
        string namespaceActorId,
        AgentProfileOperationFact operation) =>
        SendToAsync(
            namespaceActorId,
            new AgentProfileInitializedContinuation
            {
                Operation = operation.Clone(),
                Identity = State.Identity.Clone(),
                ProfileActorId = Id,
                DraftRevision = State.DraftRevision,
                DraftSha256 = State.DraftSha256,
            },
            CancellationToken.None);

    private Task SendInitializationRejectedAsync(
        string namespaceActorId,
        AgentProfileOperationFact operation,
        AgentProfileIdentity? identity,
        AgentProfileSafeDiagnostic diagnostic) =>
        SendToAsync(
            namespaceActorId,
            new AgentProfileInitializationRejectedContinuation
            {
                Operation = operation.Clone(),
                Identity = identity?.Clone() ?? new AgentProfileIdentity(),
                ProfileActorId = Id,
                Diagnostic = diagnostic.Clone(),
            },
            CancellationToken.None);

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
        _ = state;
        var next = new AgentProfileState
        {
            Identity = evt.Identity.Clone(),
            NamespaceActorId = evt.NamespaceActorId,
            Draft = evt.InitialContent.Clone(),
            DraftRevision = evt.DraftRevision,
            DraftSha256 = evt.DraftSha256,
        };
        next.Operations.Add(new AgentProfileOperationState
        {
            Operation = evt.Operation.Clone(),
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
