using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class AgentProfileNamespaceGAgentTests
{
    [Fact]
    public async Task Create_ShouldCommitFirstHandleClaimAndSendInitializationContinuationRequest()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = CreateCommand();

        await agent.HandleCreateAsync(command);

        agent.State.HandleClaims.Should().ContainSingle();
        agent.State.HandleClaims[0].OwnerHandle.Should().Be("alice");
        agent.State.HandleClaims[0].Owner.Should().BeEquivalentTo(command.Identity.Owner);
        agent.State.Profiles.Should().ContainSingle();
        agent.State.Profiles[0].Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        agent.State.Profiles[0].Identity.Should().BeEquivalentTo(command.Identity);
        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().ContainSingle();
        publisher.Sends[0].TargetActorId.Should().Be(command.ProfileActorId);
        var initialization = publisher.Sends[0].Payload.Unpack<InitializeAgentProfileCommand>();
        initialization.Identity.Should().BeEquivalentTo(command.Identity);
        initialization.InitialContent.Should().BeEquivalentTo(
            AgentProfileDeterminism.NormalizeContent(command.InitialContent));
        initialization.NamespaceActorId.Should().Be(agent.Id);
    }

    [Fact]
    public async Task Create_ShouldReuseSameOwnersHandleAcrossDistinctScopes()
    {
        var (agent, _, _) = await CreateActorAsync();
        var first = CreateCommand();
        var secondIdentity = GAgentServiceTestKit.CreateAgentProfileIdentity(
            profileId: "prof-beta",
            scopeId: "scope-beta",
            profileSlug: "researcher");
        var second = CreateCommand(secondIdentity, operationId: "op-create-beta", profileActorId: "profile-beta");

        await agent.HandleCreateAsync(first);
        await agent.HandleCreateAsync(second);

        agent.State.HandleClaims.Should().ContainSingle();
        agent.State.Profiles.Should().HaveCount(2);
        agent.State.Profiles.Select(static entry => entry.Identity.OwningScopeId)
            .Should().BeEquivalentTo(["scope-alpha", "scope-beta"]);
    }

    [Fact]
    public async Task Create_ShouldRejectSameOwnerSwitchingCommittedHandle()
    {
        var (agent, store, _) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var switched = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(
                profileId: "prof-beta",
                ownerHandle: "alice-two",
                profileSlug: "researcher"),
            operationId: "op-switch-handle",
            profileActorId: "profile-beta");

        await agent.HandleCreateAsync(switched);

        agent.State.HandleClaims.Should().ContainSingle(x => x.OwnerHandle == "alice");
        agent.State.Profiles.Should().ContainSingle();
        agent.State.Operations.Single(x => x.Operation.OperationId == "op-switch-handle")
            .Diagnostic.Code.Should().Be("OWNER_HANDLE_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_ShouldRejectCrossOwnerHandleCollision()
    {
        var (agent, _, _) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var conflicting = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(
                profileId: "prof-beta",
                ownerSubjectId: "owner-beta",
                ownerHandle: "alice",
                profileSlug: "researcher"),
            operationId: "op-owner-conflict",
            profileActorId: "profile-beta");

        await agent.HandleCreateAsync(conflicting);

        agent.State.Profiles.Should().ContainSingle();
        agent.State.Operations.Single(x => x.Operation.OperationId == "op-owner-conflict")
            .Diagnostic.Code.Should().Be("OWNER_HANDLE_CONFLICT");
    }

    [Fact]
    public async Task Create_ShouldRejectGloballyDuplicateHumanReference()
    {
        var (agent, _, _) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var duplicate = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(
                profileId: "prof-beta",
                scopeId: "scope-beta"),
            operationId: "op-slug-conflict",
            profileActorId: "profile-beta");

        await agent.HandleCreateAsync(duplicate);

        agent.State.Profiles.Should().ContainSingle();
        agent.State.Operations.Single(x => x.Operation.OperationId == "op-slug-conflict")
            .Diagnostic.Code.Should().Be("PROFILE_SLUG_TAKEN");
    }

    [Fact]
    public async Task Create_ShouldRejectDuplicateProfileIdWithDifferentReference()
    {
        var (agent, store, _) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var duplicateProfileId = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(profileSlug: "researcher"),
            operationId: "op-profile-id-conflict",
            profileActorId: "profile-beta");

        await agent.HandleCreateAsync(duplicateProfileId);

        agent.State.Profiles.Should().ContainSingle();
        agent.State.Operations.Single(x => x.Operation.OperationId == "op-profile-id-conflict")
            .Diagnostic.Code.Should().Be("PROFILE_ID_TAKEN");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateConflict_ShouldNotFailExistingProvisioningEntry()
    {
        var (agent, store, _) = await CreateActorAsync();
        var original = CreateCommand();
        await agent.HandleCreateAsync(original);
        var conflict = CreateCommand(operationId: "op-profile-existing-conflict");

        await agent.HandleCreateAsync(conflict);

        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        agent.State.Operations.Single(x => x.Operation.OperationId == original.Operation.OperationId)
            .Diagnostic.Should().BeNull();
        agent.State.Operations.Single(x => x.Operation.OperationId == conflict.Operation.OperationId)
            .Diagnostic.Code.Should().Be("PROFILE_ID_TAKEN");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false, "identity")]
    [InlineData(false, "content")]
    [InlineData(true, "identity")]
    [InlineData(true, "content")]
    public async Task MalformedCreateReplayAgainstExistingEntry_ShouldNotMutateAuthority(
        bool activate,
        string boundary)
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var original = CreateCommand();
        await agent.HandleCreateAsync(original);
        if (activate)
            await agent.HandleInitializedAsync(Initialized(original));
        var version = agent.EventSourcing!.CurrentVersion;
        var malicious = original.Clone();
        if (boundary == "identity")
            malicious.Identity.Reference.ProfileSlug = "INVALID";
        else
            malicious.InitialContent.DisplayName = new string('x', 257);

        var act = () => agent.HandleCreateAsync(malicious);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        agent.State.Profiles.Should().ContainSingle().Which.Status.Should().Be(
            activate
                ? AgentProfileProvisioningStatus.Active
                : AgentProfileProvisioningStatus.Provisioning);
        agent.State.Operations.Should().ContainSingle()
            .Which.Diagnostic.Should().BeNull();
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)version);
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task MalformedCreateRejection_ShouldReplayExactlyRejectDriftAndLeaveCoordinatesReusable()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var malformed = CreateCommand();
        malformed.InitialContent.DisplayName = new string('x', 257);
        await agent.HandleCreateAsync(malformed);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        var replay = malformed.Clone();
        replay.Operation.CommandId = "cmd-malformed-create-retry";
        replay.Operation.CorrelationId = "corr-malformed-create-retry";

        await agent.HandleCreateAsync(replay);

        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        agent.State.Profiles.Should().BeEmpty();
        agent.State.HandleClaims.Should().BeEmpty();
        agent.State.Operations.Should().ContainSingle()
            .Which.Diagnostic.Code.Should().Be("INVALID_DISPLAY_NAME");
        publisher.Sends.Should().BeEmpty();

        var drifted = malformed.Clone();
        drifted.InitialContent.DisplayName = new string('y', 257);
        var driftAct = () => agent.HandleCreateAsync(drifted);
        var driftException = await driftAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        driftException.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);

        var valid = CreateCommand(operationId: "op-create-after-malformed");
        await agent.HandleCreateAsync(valid);

        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion + 1);
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task Continuation_ShouldRejectPreCreateFailureWithMatchingProfileCoordinates()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var conflict = CreateCommand(operationId: "op-profile-existing-continuation-conflict");
        await agent.HandleCreateAsync(conflict);

        var act = () => agent.HandleInitializedAsync(Initialized(conflict));

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROVISIONING_CONTINUATION_MISMATCH");
        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_ShouldRejectDuplicateProfileActorIdForDifferentProfile()
    {
        var (agent, store, _) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var duplicateActorId = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(
                profileId: "prof-beta",
                profileSlug: "researcher"),
            operationId: "op-profile-actor-conflict",
            profileActorId: "profile-alpha");

        await agent.HandleCreateAsync(duplicateActorId);

        agent.State.Profiles.Should().ContainSingle();
        agent.State.Operations.Single(x => x.Operation.OperationId == "op-profile-actor-conflict")
            .Diagnostic.Code.Should().Be("PROFILE_ACTOR_ID_TAKEN");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_ShouldRejectReservedSystemHandleForUserOwner()
    {
        var (agent, _, publisher) = await CreateActorAsync();
        var command = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(ownerHandle: "system"),
            operationId: "op-reserved-system");

        await agent.HandleCreateAsync(command);

        agent.State.Profiles.Should().BeEmpty();
        agent.State.HandleClaims.Should().BeEmpty();
        agent.State.Operations.Should().ContainSingle()
            .Which.Diagnostic.Code.Should().Be("RESERVED_OWNER_HANDLE");
        publisher.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ReplayShouldResendWhileProvisioningWithoutCommittingAnotherEvent()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-create-retry";
        replay.Operation.CorrelationId = "corr-create-retry";

        await agent.HandleCreateAsync(replay);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().HaveCount(2);
        publisher.Sends[1].Payload.Unpack<InitializeAgentProfileCommand>()
            .Operation.CommandId.Should().Be("cmd-create-retry");
    }

    [Fact]
    public async Task Create_ReusedOperationWithPayloadDriftShouldBeRejectedWithoutMutation()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        await agent.HandleCreateAsync(CreateCommand());
        var drifted = CreateCommand(
            content: GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Changed"));

        var act = () => agent.HandleCreateAsync(drifted);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateReplay_ShouldRejectIdentityDriftWithOriginalOperationDigest()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        var replay = command.Clone();
        replay.Identity.Reference.ProfileSlug = "researcher";

        var act = () => agent.HandleCreateAsync(replay);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task InitializedContinuation_ShouldMoveProvisioningEntryToActive()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);

        await agent.HandleInitializedAsync(Initialized(command));

        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Active);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FailedContinuation_ShouldRemainDurableAndSameOperationRetryShouldResendInitialization()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        await agent.HandleInitializationRejectedAsync(new AgentProfileInitializationRejectedContinuation
        {
            Operation = command.Operation.Clone(),
            Identity = command.Identity.Clone(),
            ProfileActorId = command.ProfileActorId,
            Diagnostic = new AgentProfileSafeDiagnostic
            {
                Code = "PROFILE_INITIALIZATION_REJECTED",
                Message = "Initialization was rejected.",
                Path = "identity",
            },
        });

        await agent.HandleCreateAsync(command.Clone());

        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Failed);
        agent.State.Profiles[0].Failure.Code.Should().Be("PROFILE_INITIALIZATION_REJECTED");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        publisher.Sends.Should().HaveCount(2);
    }

    [Fact]
    public async Task FailedContinuation_ReplayCannotChangeCommittedFailure()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        var rejected = new AgentProfileInitializationRejectedContinuation
        {
            Operation = command.Operation.Clone(),
            Identity = command.Identity.Clone(),
            ProfileActorId = command.ProfileActorId,
            Diagnostic = new AgentProfileSafeDiagnostic
            {
                Code = "PROFILE_INITIALIZATION_REJECTED",
                Message = "Initialization was rejected.",
                Path = "identity",
            },
        };
        await agent.HandleInitializationRejectedAsync(rejected);
        var changed = rejected.Clone();
        changed.Diagnostic.Code = "DIFFERENT_FAILURE";

        var act = () => agent.HandleInitializationRejectedAsync(changed);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROVISIONING_CONTINUATION_MISMATCH");
        agent.State.Profiles[0].Failure.Code.Should().Be("PROFILE_INITIALIZATION_REJECTED");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ActiveCreateReplay_ShouldBeNoOpWithoutResendingInitialization()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        await agent.HandleInitializedAsync(Initialized(command));

        await agent.HandleCreateAsync(command.Clone());

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        publisher.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task Continuation_ShouldRejectUnknownOrMismatchedProvisioningWithoutMutation()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        var unknown = Initialized(command);
        unknown.Identity.ProfileId = "prof-unknown";

        var unknownAct = () => agent.HandleInitializedAsync(unknown);
        var unknownException = await unknownAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        unknownException.Which.Code.Should().Be("UNKNOWN_PROFILE_PROVISIONING");

        var mismatched = Initialized(command);
        mismatched.ProfileActorId = "profile-other";
        var mismatchAct = () => agent.HandleInitializedAsync(mismatched);
        var mismatchException = await mismatchAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        mismatchException.Which.Code.Should().Be("PROFILE_PROVISIONING_CONTINUATION_MISMATCH");
        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Continuation_ShouldRejectOperationMisassociatedWithAnotherEntryActor()
    {
        var (agent, store, _) = await CreateActorAsync();
        var first = CreateCommand();
        await agent.HandleCreateAsync(first);
        var conflicting = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(profileSlug: "researcher"),
            operationId: "op-misassociated",
            profileActorId: "profile-beta");
        await agent.HandleCreateAsync(conflicting);
        var misassociated = Initialized(conflicting);
        misassociated.Identity = first.Identity.Clone();
        misassociated.ProfileActorId = first.ProfileActorId;

        var act = () => agent.HandleInitializedAsync(misassociated);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROVISIONING_CONTINUATION_MISMATCH");
        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task PublishedSummary_ShouldOnlyAdvanceMappedProfileAndIgnoreExactReplayOrStaleRevision()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        await agent.HandleInitializedAsync(Initialized(command));
        var first = Summary(command, "op-publish-one", revision: 1, digestByte: 0x31);

        await agent.HandleObservePublishedSummaryAsync(first);
        var versionAfterFirst = agent.EventSourcing!.CurrentVersion;
        await agent.HandleObservePublishedSummaryAsync(first.Clone());
        await agent.HandleObservePublishedSummaryAsync(
            Summary(command, "op-publish-stale", revision: 0, digestByte: 0x30));

        agent.EventSourcing.CurrentVersion.Should().Be(versionAfterFirst);
        agent.State.Profiles[0].PublishedSummary.PublishedRevision.Should().Be(1);
        agent.State.Profiles[0].PublishedSummary.SnapshotSha256.Should().Equal(Digest(0x31));
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(3);

        await agent.HandleObservePublishedSummaryAsync(
            Summary(command, "op-publish-two", revision: 2, digestByte: 0x32));

        agent.State.Profiles[0].PublishedSummary.PublishedRevision.Should().Be(2);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(4);
    }

    [Fact]
    public async Task PublishedSummary_ShouldRejectProfileOutsideMappedIdentity()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand();
        await agent.HandleCreateAsync(command);
        await agent.HandleInitializedAsync(Initialized(command));
        var observation = Summary(command, "op-publish-other", revision: 1, digestByte: 0x41);
        observation.Identity.ProfileId = "prof-other";

        var act = () => agent.HandleObservePublishedSummaryAsync(observation);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PUBLISHED_SUMMARY_MISMATCH");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    private static async Task<(AgentProfileNamespaceGAgent Agent, InMemoryEventStore Store, RecordingProfileEventPublisher Publisher)>
        CreateActorAsync()
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingProfileEventPublisher();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
            store,
            AgentProfileActorIds.Namespace,
            static () => new AgentProfileNamespaceGAgent());
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        return (agent, store, publisher);
    }

    private static CreateAgentProfileCommand CreateCommand(
        AgentProfileIdentity? identity = null,
        AgentProfileContent? content = null,
        string operationId = "op-create-alpha",
        string profileActorId = "profile-alpha")
    {
        identity ??= GAgentServiceTestKit.CreateAgentProfileIdentity();
        content ??= GAgentServiceTestKit.CreateAgentProfileContent();
        ByteString inputSha256;
        try
        {
            inputSha256 = AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content);
        }
        catch (AgentProfileContractValidationException)
        {
            inputSha256 = Digest(0x11);
        }

        return new CreateAgentProfileCommand
        {
            Identity = identity.Clone(),
            InitialContent = content.Clone(),
            ProfileActorId = profileActorId,
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                inputSha256),
        };
    }

    private static AgentProfileInitializedContinuation Initialized(CreateAgentProfileCommand command) =>
        new()
        {
            Operation = command.Operation.Clone(),
            Identity = command.Identity.Clone(),
            ProfileActorId = command.ProfileActorId,
            DraftRevision = 1,
            DraftSha256 = AgentProfileDeterminism.ComputeDraftSha256(command.InitialContent),
        };

    private static ObserveAgentProfilePublishedSummaryCommand Summary(
        CreateAgentProfileCommand command,
        string operationId,
        long revision,
        byte digestByte) =>
        new()
        {
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(operationId, Digest(digestByte)),
            Identity = command.Identity.Clone(),
            Summary = new AgentProfilePublishedSummary
            {
                Reference = command.Identity.Reference.Clone(),
                DisplayName = command.InitialContent.DisplayName,
                Purpose = command.InitialContent.Purpose,
                PublishedRevision = revision,
                SnapshotSha256 = Digest(digestByte),
            },
        };

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());
}
