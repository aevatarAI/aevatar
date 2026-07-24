using System.Text;
using Aevatar.Foundation.Abstractions;
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
    public async Task Create_ShouldRejectInvalidIngressProofBeforeOperationParsingOrPersistence()
    {
        var verifier = new RecordingIngressProofVerifier(false);
        var (agent, store, publisher) = await CreateActorAsync(verifier);
        var command = CreateCommand();
        command.Operation = null!;

        var act = () => agent.HandleCreateAsync(command);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_INGRESS_PROOF_INVALID");
        verifier.Calls.Should().ContainSingle()
            .Which.TargetActorId.Should().Be(agent.Id);
        agent.State.Operations.Should().BeEmpty();
        (await store.GetEventsAsync(agent.Id)).Should().BeEmpty();
        publisher.Sends.Should().BeEmpty();
    }

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
            await DispatchInitializedAsync(agent, Initialized(original));
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

        var act = () => DispatchInitializedAsync(agent, Initialized(conflict));

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
    public async Task Create_ShouldCommitReservedPlatformScopeFailureWithoutOrdinaryProfileFact()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var identity = GAgentServiceTestKit.CreateAgentProfileIdentity();
        identity.OwningScopeId = PlatformScopeSemantics.ReservedPlatformScopeId;
        var command = CreateCommand(identity, operationId: "op-reserved-platform-scope");

        await agent.HandleCreateAsync(command);

        agent.State.Profiles.Should().BeEmpty();
        agent.State.HandleClaims.Should().BeEmpty();
        agent.State.Operations.Should().ContainSingle()
            .Which.Diagnostic.Code.Should().Be("RESERVED_OWNING_SCOPE_ID");
        publisher.Sends.Should().BeEmpty();
        var committed = (await store.GetEventsAsync(agent.Id)).Should().ContainSingle().Which;
        committed.EventData.Is(AgentProfileProvisioningFailedEvent.Descriptor).Should().BeTrue();
        committed.EventData.Unpack<AgentProfileProvisioningFailedEvent>()
            .Identity.Should().BeEquivalentTo(new AgentProfileIdentity());
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

        await DispatchInitializedAsync(agent, Initialized(command));

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
        await DispatchInitializationRejectedAsync(agent, new AgentProfileInitializationRejectedContinuation
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
        await DispatchInitializationRejectedAsync(agent, rejected);
        var changed = rejected.Clone();
        changed.Diagnostic.Code = "DIFFERENT_FAILURE";

        var act = () => DispatchInitializationRejectedAsync(agent, changed);

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
        await DispatchInitializedAsync(agent, Initialized(command));

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

        var unknownAct = () => DispatchInitializedAsync(agent, unknown);
        var unknownException = await unknownAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        unknownException.Which.Code.Should().Be("UNKNOWN_PROFILE_PROVISIONING");

        var mismatched = Initialized(command);
        mismatched.ProfileActorId = "profile-other";
        var mismatchAct = () => DispatchInitializedAsync(agent, mismatched);
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

        var act = () => DispatchInitializedAsync(agent, misassociated);

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
        await DispatchInitializedAsync(agent, Initialized(command));
        var first = Summary(command, "op-publish-one", revision: 1, digestByte: 0x31);

        await DispatchPublishedSummaryAsync(agent, first);
        var versionAfterFirst = agent.EventSourcing!.CurrentVersion;
        var replay = first.Clone();
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            replay.Operation,
            "published-summary-retry");
        await DispatchPublishedSummaryAsync(agent, replay);
        await DispatchPublishedSummaryAsync(agent,
            Summary(command, "op-publish-stale", revision: 0, digestByte: 0x30));

        agent.EventSourcing.CurrentVersion.Should().Be(versionAfterFirst);
        agent.State.Profiles[0].PublishedSummary.PublishedRevision.Should().Be(1);
        agent.State.Profiles[0].PublishedSummary.SnapshotSha256.Should().Equal(Digest(0x31));
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(3);

        await DispatchPublishedSummaryAsync(agent,
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
        await DispatchInitializedAsync(agent, Initialized(command));
        var observation = Summary(command, "op-publish-other", revision: 1, digestByte: 0x41);
        observation.Identity.ProfileId = "prof-other";

        var act = () => DispatchPublishedSummaryAsync(agent, observation);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PUBLISHED_SUMMARY_MISMATCH");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("initialized")]
    [InlineData("rejected")]
    [InlineData("summary")]
    public async Task ProfileProtocolIngress_ShouldRejectSpoofedEnvelopePublisher(
        string messageKind)
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: $"op-spoof-{messageKind}");
        await agent.HandleCreateAsync(command);
        IMessage payload;
        switch (messageKind)
        {
            case "initialized":
                payload = Initialized(command);
                break;
            case "rejected":
                payload = new AgentProfileInitializationRejectedContinuation
                {
                    Operation = command.Operation.Clone(),
                    Identity = command.Identity.Clone(),
                    ProfileActorId = command.ProfileActorId,
                    Diagnostic = new AgentProfileSafeDiagnostic { Code = "INITIALIZATION_FAILED" },
                };
                break;
            case "summary":
                await DispatchInitializedAsync(agent, Initialized(command));
                payload = Summary(command, "op-spoof-summary-publish", revision: 1, digestByte: 0x62);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(messageKind));
        }
        var version = agent.EventSourcing!.CurrentVersion;

        var act = () => GAgentServiceTestKit.DispatchAsync(agent, payload, "profile-spoof");

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROTOCOL_PUBLISHER_MISMATCH");
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)version);
    }

    [Theory]
    [InlineData("initialized", null)]
    [InlineData("initialized", "attacker-actor")]
    [InlineData("rejected", null)]
    [InlineData("rejected", "attacker-actor")]
    [InlineData("summary", null)]
    [InlineData("summary", "attacker-actor")]
    public async Task ProfileProtocolIngress_ShouldRejectMissingOrForgedAuthenticatedOriginWhenLegacyOriginsMatch(
        string messageKind,
        string? authenticatedActorId)
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: $"op-forged-origin-{messageKind}");
        await agent.HandleCreateAsync(command);
        IMessage payload;
        switch (messageKind)
        {
            case "initialized":
                payload = Initialized(command);
                break;
            case "rejected":
                payload = new AgentProfileInitializationRejectedContinuation
                {
                    Operation = command.Operation.Clone(),
                    Identity = command.Identity.Clone(),
                    ProfileActorId = command.ProfileActorId,
                    Diagnostic = new AgentProfileSafeDiagnostic { Code = "INITIALIZATION_FAILED" },
                };
                break;
            case "summary":
                await DispatchInitializedAsync(agent, Initialized(command));
                payload = Summary(
                    command,
                    "op-forged-origin-summary-publish",
                    revision: long.MaxValue,
                    digestByte: 0x63);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(messageKind));
        }
        var version = agent.EventSourcing!.CurrentVersion;

        var act = () => GAgentServiceTestKit.DispatchAsync(
            agent,
            payload,
            command.ProfileActorId,
            authenticatedActorId);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROTOCOL_PUBLISHER_MISMATCH");
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)version);
    }

    [Fact]
    public async Task InitializationRejection_ShouldBoundEveryPersistedDiagnosticField()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: "op-bounded-ingress-diagnostic");
        await agent.HandleCreateAsync(command);
        var continuation = new AgentProfileInitializationRejectedContinuation
        {
            Operation = command.Operation.Clone(),
            Identity = command.Identity.Clone(),
            ProfileActorId = command.ProfileActorId,
            Diagnostic = new AgentProfileSafeDiagnostic
            {
                Code = new string('C', 600),
                Message = new string('\u00e9', 600),
                Path = new string('\u00e9', 600),
            },
        };

        await DispatchInitializationRejectedAsync(agent, continuation);

        var diagnostic = agent.State.Profiles.Single().Failure;
        Encoding.UTF8.GetByteCount(diagnostic.Code).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Message).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Path).Should().BeLessThanOrEqualTo(512);
        var persisted = (await store.GetEventsAsync(agent.Id))[^1].EventData
            .Unpack<AgentProfileProvisioningFailedEvent>();
        persisted.Diagnostic.Should().Be(diagnostic);
    }

    [Theory]
    [InlineData("display_name")]
    [InlineData("purpose")]
    public async Task PublishedSummary_ShouldRejectOversizedMultibyteAuthoredText(string field)
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: $"op-summary-oversized-{field}");
        await agent.HandleCreateAsync(command);
        await DispatchInitializedAsync(agent, Initialized(command));
        var summary = Summary(command, $"op-summary-publish-{field}", revision: 1, digestByte: 0x63);
        if (field == "display_name")
            summary.Summary.DisplayName = new string('\u00e9', 129);
        else
            summary.Summary.Purpose = new string('\u00e9', 2_049);
        var version = agent.EventSourcing!.CurrentVersion;

        var act = () => DispatchPublishedSummaryAsync(agent, summary);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PUBLISHED_SUMMARY_MISMATCH");
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)version);
    }

    [Fact]
    public async Task CreateBadDigest_ExactReplayShouldIgnoreCallerDigest()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: "op-create-bad-digest-authority");
        command.Operation.InputSha256 = Digest(0x81);
        await agent.HandleCreateAsync(command);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        var replay = command.Clone();
        replay.Operation.InputSha256 = Digest(0x82);
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            replay.Operation,
            "create-bad-digest-retry");

        await agent.HandleCreateAsync(replay);

        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        agent.State.Operations.Should().ContainSingle()
            .Which.Diagnostic.Code.Should().Be("OPERATION_INPUT_SHA256_MISMATCH");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task CreateDigestAlias_ShouldNotReplayDifferentPayload()
    {
        var (agent, store, _) = await CreateActorAsync();
        const string operationId = "op-create-digest-alias";
        var first = CreateCommand(operationId: operationId);
        var second = CreateCommand(
            GAgentServiceTestKit.CreateAgentProfileIdentity(
                profileId: "prof-beta",
                profileSlug: "researcher"),
            operationId: operationId);
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            second.Operation,
            "create-digest-alias-second");
        first.Operation.InputSha256 = second.Operation.InputSha256;
        await agent.HandleCreateAsync(first);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;

        var act = () => agent.HandleCreateAsync(second);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task CanonicalCreate_ShouldNotReplayPrecanonicalRejection()
    {
        var (agent, store, _) = await CreateActorAsync();
        const string operationId = "op-create-precanonical-boundary";
        var valid = CreateCommand(operationId: operationId);
        var malformed = valid.Clone();
        malformed.InitialContent.DisplayName = string.Empty;
        malformed.Operation.InputSha256 = valid.Operation.InputSha256;
        await agent.HandleCreateAsync(malformed);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            valid.Operation,
            "canonical-create-second");

        var act = () => agent.HandleCreateAsync(valid);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        agent.State.Profiles.Should().BeEmpty();
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task CreateOperationIdReuseForPublishedSummary_ShouldConflictByKind()
    {
        var (agent, store, _) = await CreateActorAsync();
        var command = CreateCommand(operationId: "op-create-summary-cross-family");
        await agent.HandleCreateAsync(command);
        await DispatchInitializedAsync(agent, Initialized(command));
        var summary = Summary(command, command.Operation.OperationId, revision: 1, digestByte: 0x61);
        summary.Operation.InputSha256 = command.Operation.InputSha256;
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            summary.Operation,
            "create-summary-cross-family-second");
        var version = agent.EventSourcing!.CurrentVersion;

        var act = () => DispatchPublishedSummaryAsync(agent, summary);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)version);
    }

    [Fact]
    public async Task OperationRetention_ShouldBoundTerminalWindowAndPinIncompleteProvisioning()
    {
        var (agent, store, _) = await CreateActorAsync();
        var pinned = CreateCommand(operationId: "op-pinned-provisioning");
        await agent.HandleCreateAsync(pinned);
        var terminalCommands = new List<CreateAgentProfileCommand>();
        var totalTerminalOperations =
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations + 76;
        var serializedSizeAtLimit = 0;

        for (var index = 0; index < totalTerminalOperations; index++)
        {
            var command = CreateCommand(operationId: $"op-terminal-{index:D4}");
            command.InitialContent.DisplayName = string.Empty;
            terminalCommands.Add(command.Clone());
            await agent.HandleCreateAsync(command);

            if (index == AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations - 1)
            {
                agent.State.Operations.Should().HaveCount(
                    AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations + 1);
                serializedSizeAtLimit = agent.State.CalculateSize();
            }
        }

        agent.State.Profiles.Should().ContainSingle()
            .Which.Status.Should().Be(AgentProfileProvisioningStatus.Provisioning);
        agent.State.Operations.Should().HaveCount(
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations + 1);
        agent.State.Operations.Should().Contain(operation =>
            operation.Operation.OperationId == pinned.Operation.OperationId);
        agent.State.Operations.Where(operation => !operation.ProvisioningStarted)
            .Select(operation => operation.Operation.OperationId)
            .Should().Equal(terminalCommands
                .TakeLast(AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations)
                .Select(command => command.Operation.OperationId));
        agent.State.CalculateSize().Should().Be(serializedSizeAtLimit);

        var oldestRetained = terminalCommands[
            totalTerminalOperations -
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations];
        var versionBeforeReplay = agent.EventSourcing!.CurrentVersion;
        var retainedReplay = oldestRetained.Clone();
        GAgentServiceTestKit.SetAgentProfileDispatchAttempt(
            retainedReplay.Operation,
            "retained-terminal-replay");
        await agent.HandleCreateAsync(retainedReplay);
        agent.EventSourcing.CurrentVersion.Should().Be(versionBeforeReplay);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)versionBeforeReplay);

        var retainedDrift = oldestRetained.Clone();
        retainedDrift.InitialContent.Purpose = "Drifted retained payload";
        var driftAct = () => agent.HandleCreateAsync(retainedDrift);
        var driftException = await driftAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        driftException.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(versionBeforeReplay);

        await DispatchInitializationRejectedAsync(agent, new AgentProfileInitializationRejectedContinuation
        {
            Operation = pinned.Operation.Clone(),
            Identity = pinned.Identity.Clone(),
            ProfileActorId = pinned.ProfileActorId,
            Diagnostic = new AgentProfileSafeDiagnostic
            {
                Code = "PROFILE_INITIALIZATION_REJECTED",
                Message = "Initialization was rejected.",
                Path = "identity",
            },
        });
        agent.State.Profiles.Single().Status.Should().Be(AgentProfileProvisioningStatus.Failed);
        agent.State.Operations.Should().Contain(operation =>
            operation.Operation.OperationId == pinned.Operation.OperationId);

        var newestTerminal = CreateCommand(operationId: "op-terminal-1100");
        newestTerminal.InitialContent.DisplayName = string.Empty;
        await agent.HandleCreateAsync(newestTerminal);
        agent.State.Operations.Should().HaveCount(
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations + 1);
        agent.State.Operations.Should().Contain(operation =>
            operation.Operation.OperationId == pinned.Operation.OperationId);

        await DispatchInitializedAsync(agent, Initialized(pinned));

        agent.State.Profiles.Single().Status.Should().Be(AgentProfileProvisioningStatus.Active);
        agent.State.Operations.Should().HaveCount(
            AgentProfileOperationRetentionPolicy.MaxRetainedNamespaceTerminalOperations);
        agent.State.Operations.Should().NotContain(operation =>
            operation.Operation.OperationId == pinned.Operation.OperationId);
    }

    private static async Task<(AgentProfileNamespaceGAgent Agent, InMemoryEventStore Store, RecordingProfileEventPublisher Publisher)>
        CreateActorAsync(IAgentProfileIngressProofVerifier? verifier = null)
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingProfileEventPublisher();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
            store,
            AgentProfileActorIds.Namespace,
            () => new AgentProfileNamespaceGAgent(
                verifier ?? AcceptingIngressProofVerifier.Instance));
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        return (agent, store, publisher);
    }

    private static Task DispatchInitializedAsync(
        AgentProfileNamespaceGAgent agent,
        AgentProfileInitializedContinuation continuation,
        string? publisherActorId = null) =>
        GAgentServiceTestKit.DispatchAsync(
            agent,
            continuation,
            publisherActorId ?? continuation.ProfileActorId);

    private static Task DispatchInitializationRejectedAsync(
        AgentProfileNamespaceGAgent agent,
        AgentProfileInitializationRejectedContinuation continuation,
        string? publisherActorId = null) =>
        GAgentServiceTestKit.DispatchAsync(
            agent,
            continuation,
            publisherActorId ?? continuation.ProfileActorId);

    private static Task DispatchPublishedSummaryAsync(
        AgentProfileNamespaceGAgent agent,
        ObserveAgentProfilePublishedSummaryCommand command,
        string publisherActorId = "profile-alpha") =>
        GAgentServiceTestKit.DispatchAsync(agent, command, publisherActorId);

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

    private sealed class AcceptingIngressProofVerifier : IAgentProfileIngressProofVerifier
    {
        public static AcceptingIngressProofVerifier Instance { get; } = new();

        public bool Verify(string targetActorId, IMessage command) => true;
    }

    private sealed class RecordingIngressProofVerifier(bool result) : IAgentProfileIngressProofVerifier
    {
        public List<(string TargetActorId, IMessage Command)> Calls { get; } = [];

        public bool Verify(string targetActorId, IMessage command)
        {
            Calls.Add((targetActorId, command));
            return result;
        }
    }
}
