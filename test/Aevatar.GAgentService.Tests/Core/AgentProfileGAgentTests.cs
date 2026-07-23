using System.Text;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class AgentProfileGAgentTests
{
    private const string SkillGuidAlpha = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";
    private const string SkillGuidBeta = "bbde72de-cf15-4eef-a8cb-10d8f9b24a53";

    [Fact]
    public async Task Initialize_ShouldCommitImmutableIdentityAndClonedInitialDraftThenSendContinuation()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();

        await DispatchInitializeAsync(agent, command);
        command.Identity.ProfileId = "mutated-after-commit";
        command.InitialContent.DisplayName = "Mutated after commit";

        agent.State.Identity.ProfileId.Should().Be("prof-alpha");
        agent.State.NamespaceActorId.Should().Be(AgentProfileActorIds.Namespace);
        agent.State.Draft.DisplayName.Should().Be("Assistant");
        agent.State.DraftRevision.Should().Be(1);
        agent.State.DraftSha256.Should().Equal(
            AgentProfileDeterminism.ComputeDraftSha256(GAgentServiceTestKit.CreateAgentProfileContent()));
        agent.State.Published.Should().BeNull();
        agent.State.PublishedRevision.Should().Be(0);
        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().ContainSingle();
        publisher.Sends[0].TargetActorId.Should().Be(AgentProfileActorIds.Namespace);
        var continuation = publisher.Sends[0].Payload.Unpack<AgentProfileInitializedContinuation>();
        continuation.ProfileActorId.Should().Be(agent.Id);
        continuation.DraftRevision.Should().Be(1);
        continuation.DraftSha256.Should().Equal(agent.State.DraftSha256);
    }

    [Fact]
    public async Task Initialize_ShouldRejectSpoofedEnvelopePublisherBeforeCommit()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand(operationId: "op-initialize-spoofed-publisher");

        var act = () => DispatchInitializeAsync(agent, command, "namespace-spoof");

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("PROFILE_PROTOCOL_PUBLISHER_MISMATCH");
        agent.State.Identity.Should().BeNull();
        (await store.GetEventsAsync(agent.Id)).Should().BeEmpty();
        publisher.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Initialize_IdenticalOperationReplayShouldResendContinuationWithoutNewEvent()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        await DispatchInitializeAsync(agent, command);
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-initialize-retry";
        replay.Operation.CorrelationId = "corr-initialize-retry";

        await DispatchInitializeAsync(agent, replay);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().HaveCount(2);
        publisher.Sends[1].Payload.Unpack<AgentProfileInitializedContinuation>()
            .Operation.CommandId.Should().Be("cmd-initialize-retry");
    }

    [Fact]
    public async Task InitializeReplay_ShouldUseCommittedContinuationAfterFirstSendFailsAndDraftChanges()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        publisher.SendFailuresRemaining = 1;
        var command = InitializeCommand();
        var initialDraftSha256 = AgentProfileDeterminism.ComputeDraftSha256(command.InitialContent);

        var initialize = () => DispatchInitializeAsync(agent, command);
        await initialize.Should().ThrowAsync<InvalidOperationException>();

        var changed = agent.State.Draft.Clone();
        changed.DisplayName = "Changed after initialization";
        await agent.HandleUpdateDraftAsync(UpdateCommand(
            agent.State.Identity,
            changed,
            "op-update-after-initialize-send-failure",
            expectedVersion: 1));
        agent.State.DraftRevision.Should().Be(2);

        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-initialize-recovery";
        replay.Operation.CorrelationId = "corr-initialize-recovery";
        await DispatchInitializeAsync(agent, replay);

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        publisher.Sends.Should().ContainSingle();
        var continuation = publisher.Sends[0].Payload.Unpack<AgentProfileInitializedContinuation>();
        continuation.Operation.CommandId.Should().Be("cmd-initialize-recovery");
        continuation.DraftRevision.Should().Be(1);
        continuation.DraftSha256.Should().Equal(initialDraftSha256);
    }

    [Fact]
    public async Task InitializeRejection_ShouldCommitBeforeSendAndReplayStoredContinuation()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        publisher.SendFailuresRemaining = 1;
        var command = InitializeCommand(content: ContentWithMultipleDefaultBindings());

        var initialize = () => DispatchInitializeAsync(agent, command);
        await initialize.Should().ThrowAsync<InvalidOperationException>();

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        agent.State.Operations.Should().ContainSingle();
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-initialize-rejection-recovery";
        replay.Operation.CorrelationId = "corr-initialize-rejection-recovery";
        await DispatchInitializeAsync(agent, replay);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().ContainSingle();
        var continuation = publisher.Sends[0].Payload.Unpack<AgentProfileInitializationRejectedContinuation>();
        continuation.Operation.CommandId.Should().Be("cmd-initialize-rejection-recovery");
        continuation.Identity.Should().BeEquivalentTo(command.Identity);
        continuation.ProfileActorId.Should().Be(agent.Id);
        continuation.Diagnostic.Code.Should().Be("MULTIPLE_DEFAULT_SKILLS");
    }

    [Theory]
    [InlineData("input-digest", "OPERATION_INPUT_SHA256_MISMATCH")]
    [InlineData("default-binding", "MULTIPLE_DEFAULT_SKILLS")]
    [InlineData("identity", "PROFILE_IDENTITY_CONFLICT")]
    public async Task InitializeStoredRejection_ShouldRecoverFailedSendFromExactReplay(
        string rejection,
        string expectedDiagnostic)
    {
        var fixture = rejection == "identity"
            ? await CreateInitializedActorAsync()
            : await CreateActorAsync();
        var (agent, store, publisher) = fixture;
        var command = rejection switch
        {
            "default-binding" => InitializeCommand(
                content: ContentWithMultipleDefaultBindings(),
                operationId: "op-initialize-default-rejection"),
            "identity" => InitializeCommand(
                identity: GAgentServiceTestKit.CreateAgentProfileIdentity(
                    profileId: "prof-other",
                    profileSlug: "other"),
                operationId: "op-initialize-identity-rejection"),
            _ => InitializeCommand(operationId: "op-initialize-digest-rejection"),
        };
        if (rejection == "input-digest")
            command.Operation.InputSha256 = Digest(0x71);
        var eventCountBeforeRejection = (await store.GetEventsAsync(agent.Id)).Count;
        var sendCountBeforeRejection = publisher.Sends.Count;
        publisher.SendFailuresRemaining = 1;

        var initialize = () => DispatchInitializeAsync(agent, command);
        await initialize.Should().ThrowAsync<InvalidOperationException>();

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCountBeforeRejection + 1);
        agent.State.Operations.Single(x => x.Operation.OperationId == command.Operation.OperationId)
            .InitializationRejection.Continuation.Diagnostic.Code.Should().Be(expectedDiagnostic);
        var replay = command.Clone();
        replay.Operation.CommandId = $"cmd-{rejection}-rejection-retry";
        replay.Operation.CorrelationId = $"corr-{rejection}-rejection-retry";

        await DispatchInitializeAsync(agent, replay);

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCountBeforeRejection + 1);
        publisher.Sends.Should().HaveCount(sendCountBeforeRejection + 1);
        var continuation = publisher.Sends[^1].Payload
            .Unpack<AgentProfileInitializationRejectedContinuation>();
        continuation.Operation.CommandId.Should().Be(replay.Operation.CommandId);
        continuation.Diagnostic.Code.Should().Be(expectedDiagnostic);
    }

    [Theory]
    [InlineData("input-digest")]
    [InlineData("default-binding")]
    [InlineData("identity")]
    public async Task InitializeStoredRejection_ShouldRejectSemanticDrift(string rejection)
    {
        var fixture = rejection == "identity"
            ? await CreateInitializedActorAsync()
            : await CreateActorAsync();
        var (agent, store, publisher) = fixture;
        var command = rejection switch
        {
            "default-binding" => InitializeCommand(
                content: ContentWithMultipleDefaultBindings(),
                operationId: "op-initialize-default-drift"),
            "identity" => InitializeCommand(
                identity: GAgentServiceTestKit.CreateAgentProfileIdentity(
                    profileId: "prof-other",
                    profileSlug: "other"),
                operationId: "op-initialize-identity-drift"),
            _ => InitializeCommand(operationId: "op-initialize-digest-drift"),
        };
        if (rejection == "input-digest")
            command.Operation.InputSha256 = Digest(0x72);
        await DispatchInitializeAsync(agent, command);
        var committedVersion = agent.EventSourcing!.CurrentVersion;
        var sendCount = publisher.Sends.Count;
        var drifted = command.Clone();
        drifted.InitialContent.DisplayName = "Drifted initialization content";

        var act = () => DispatchInitializeAsync(agent, drifted);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(committedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)committedVersion);
        publisher.Sends.Should().HaveCount(sendCount);
    }

    [Fact]
    public async Task InitializeMalformedContentRejectionReplay_ShouldResendWithoutAnotherEvent()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        command.InitialContent.DisplayName = string.Empty;
        await DispatchInitializeAsync(agent, command);
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-initialize-invalid-content-retry";
        replay.Operation.CorrelationId = "corr-initialize-invalid-content-retry";

        await DispatchInitializeAsync(agent, replay);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        agent.State.Operations.Should().ContainSingle();
        publisher.Sends.Should().HaveCount(2);
        var continuation = publisher.Sends[1].Payload.Unpack<AgentProfileInitializationRejectedContinuation>();
        continuation.Operation.CommandId.Should().Be("cmd-initialize-invalid-content-retry");
        continuation.Diagnostic.Code.Should().Be("INVALID_DISPLAY_NAME");
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("namespace")]
    [InlineData("identity")]
    public async Task InitializeMalformedAuthorityInput_ShouldFailClosedWithoutEventOrSend(string boundary)
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        switch (boundary)
        {
            case "operation":
                command.Operation.OperationId = string.Empty;
                break;
            case "namespace":
                command.NamespaceActorId = string.Empty;
                break;
            case "identity":
                command.Identity = new AgentProfileIdentity();
                break;
        }

        var act = () => DispatchInitializeAsync(agent, command);

        await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        (await store.GetEventsAsync(agent.Id)).Should().BeEmpty();
        agent.State.Operations.Should().BeEmpty();
        publisher.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeSuccess_ShouldPreserveEarlierRejectedOperationFacts()
    {
        var (agent, store, _) = await CreateActorAsync();
        await DispatchInitializeAsync(agent, InitializeCommand(
            content: ContentWithMultipleDefaultBindings(),
            operationId: "op-initialize-rejected"));

        await DispatchInitializeAsync(agent, InitializeCommand(operationId: "op-initialize-valid"));

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        agent.State.Identity.Should().NotBeNull();
        agent.State.Operations.Select(x => x.Operation.OperationId).Should().BeEquivalentTo(
            ["op-initialize-rejected", "op-initialize-valid"]);
    }

    [Fact]
    public async Task Initialize_ShouldRejectPayloadDriftAndCommitImmutableIdentityChangeWithoutMutatingAuthority()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        await DispatchInitializeAsync(agent, command);
        var drifted = InitializeCommand(
            content: GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Drifted"));
        var otherIdentity = GAgentServiceTestKit.CreateAgentProfileIdentity(profileId: "prof-other");
        var changedIdentity = InitializeCommand(otherIdentity, operationId: "op-initialize-other");

        var driftedAct = () => DispatchInitializeAsync(agent, drifted);
        var exception = await driftedAct.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        await DispatchInitializeAsync(agent, changedIdentity);

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
        publisher.Sends.Should().HaveCount(2);
        publisher.Sends[1].Payload.Unpack<AgentProfileInitializationRejectedContinuation>()
            .Diagnostic.Code.Should().Be("PROFILE_IDENTITY_CONFLICT");
        agent.State.Identity.ProfileId.Should().Be("prof-alpha");
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("scope")]
    [InlineData("reference")]
    public async Task DraftMutation_ShouldRejectEveryImmutableIdentityBoundary(string boundary)
    {
        var (agent, _, _) = await CreateInitializedActorAsync();
        var identity = agent.State.Identity.Clone();
        switch (boundary)
        {
            case "profile":
                identity.ProfileId = "prof-other";
                break;
            case "owner":
                identity.Owner.User.SubjectId = "owner-other";
                break;
            case "scope":
                identity.OwningScopeId = "scope-other";
                break;
            case "reference":
                identity.Reference.ProfileSlug = "other-assistant";
                break;
        }

        await agent.HandleUpdateDraftAsync(UpdateCommand(
            identity,
            GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Changed"),
            $"op-immutable-{boundary}",
            expectedVersion: 1));

        agent.State.Identity.Should().BeEquivalentTo(GAgentServiceTestKit.CreateAgentProfileIdentity());
        agent.State.DraftRevision.Should().Be(1);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PROFILE_IDENTITY_CONFLICT");
    }

    [Fact]
    public async Task RejectedImmutableMutationReplay_ShouldNotCommitAnotherEvent()
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var otherIdentity = agent.State.Identity.Clone();
        otherIdentity.OwningScopeId = "scope-other";
        var command = UpdateCommand(
            otherIdentity,
            GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Foreign change"),
            "op-immutable-replay",
            expectedVersion: 1);
        await agent.HandleUpdateDraftAsync(command);
        var eventCount = (await store.GetEventsAsync(agent.Id)).Count;
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-immutable-retry";
        replay.Operation.CorrelationId = "corr-immutable-retry";

        await agent.HandleUpdateDraftAsync(replay);

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PROFILE_IDENTITY_CONFLICT");
    }

    [Theory]
    [InlineData("update", "identity")]
    [InlineData("update", "payload")]
    [InlineData("upsert", "identity")]
    [InlineData("upsert", "payload")]
    [InlineData("remove", "identity")]
    [InlineData("remove", "payload")]
    [InlineData("publish", "identity")]
    [InlineData("publish", "payload")]
    public async Task UncanonicalizableMutation_ExactReplayShouldNotCommitAnotherEvent(
        string mutation,
        string boundary)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var operationId = $"op-uncanonicalized-{mutation}-{boundary}";
        var command = MalformedMutationCommand(agent, mutation, boundary, operationId);

        await DispatchMutationAsync(agent, command);

        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        agent.State.LastMutation.Diagnostic.Code.Should().Be(
            boundary == "identity"
                ? "PROFILE_IDENTITY_CONFLICT"
                : mutation is "upsert" or "remove"
                    ? "INVALID_BINDING_ID"
                    : "INVALID_DISPLAY_NAME");
        var replay = CloneMutation(command);
        MutationOperation(replay).CommandId = $"cmd-{mutation}-{boundary}-retry";
        MutationOperation(replay).CorrelationId = $"corr-{mutation}-{boundary}-retry";

        await DispatchMutationAsync(agent, replay);

        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        agent.State.Operations.Count(x => x.Operation.OperationId == operationId).Should().Be(1);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Theory]
    [InlineData("update", "identity")]
    [InlineData("update", "payload")]
    [InlineData("upsert", "identity")]
    [InlineData("upsert", "payload")]
    [InlineData("remove", "identity")]
    [InlineData("remove", "payload")]
    [InlineData("publish", "identity")]
    [InlineData("publish", "payload")]
    public async Task UncanonicalizableMutation_SemanticDriftShouldConflict(
        string mutation,
        string boundary)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var command = MalformedMutationCommand(
            agent,
            mutation,
            boundary,
            $"op-uncanonicalized-{mutation}-{boundary}-drift");
        await DispatchMutationAsync(agent, command);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        var drifted = CloneMutation(command);
        InvalidateMutation(drifted, boundary, drifted: true);

        var act = () => DispatchMutationAsync(agent, drifted);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task UpdateDraft_ShouldIncrementRevisionAndKeepPublishedSnapshotSeparate()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();
        var firstPublish = PublishCommand(agent, Snapshot(agent.State.Identity, agent.State.Draft), "op-publish-before-update");
        await agent.HandlePublishAsync(firstPublish);
        var published = agent.State.Published.Clone();
        var changed = GAgentServiceTestKit.CreateAgentProfileContent(
            displayName: "Changed draft",
            instructions: "New draft instructions.");

        await agent.HandleUpdateDraftAsync(UpdateCommand(
            agent.State.Identity,
            changed,
            "op-update-draft",
            expectedVersion: 2));

        agent.State.DraftRevision.Should().Be(2);
        agent.State.Draft.DisplayName.Should().Be("Changed draft");
        agent.State.DraftSha256.Should().Equal(AgentProfileDeterminism.ComputeDraftSha256(changed));
        agent.State.Published.Should().BeEquivalentTo(published);
        agent.State.PublishedRevision.Should().Be(1);
    }

    [Fact]
    public async Task Mutation_ShouldCheckIdempotencyBeforeExpectedVersionAndRejectPayloadDrift()
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var changed = GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Changed");
        var command = UpdateCommand(agent.State.Identity, changed, "op-update-idempotent", expectedVersion: 1);
        await agent.HandleUpdateDraftAsync(command);
        var eventCount = (await store.GetEventsAsync(agent.Id)).Count;
        var replay = command.Clone();
        replay.ExpectedAuthorityStateVersion = 0;
        replay.Operation.CommandId = "cmd-update-retry";
        replay.Operation.CorrelationId = "corr-update-retry";

        await agent.HandleUpdateDraftAsync(replay);

        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);

        var drifted = UpdateCommand(
            agent.State.Identity,
            GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Different"),
            "op-update-idempotent",
            expectedVersion: agent.EventSourcing!.CurrentVersion);
        var act = () => agent.HandleUpdateDraftAsync(drifted);
        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("upsert")]
    [InlineData("remove")]
    [InlineData("publish")]
    public async Task MutationReplay_ShouldRejectCallerIdentityDriftBeforeNoOp(string mutation)
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, store, _) = await CreateInitializedActorAsync(content: content);
        Func<Task> replay;

        switch (mutation)
        {
            case "update":
            {
                var changed = agent.State.Draft.Clone();
                changed.DisplayName = "Changed";
                var command = UpdateCommand(
                    agent.State.Identity,
                    changed,
                    "op-identity-drift-update",
                    agent.EventSourcing!.CurrentVersion);
                await agent.HandleUpdateDraftAsync(command);
                var candidate = command.Clone();
                candidate.Identity.OwningScopeId = "scope-other";
                replay = () => agent.HandleUpdateDraftAsync(candidate);
                break;
            }
            case "upsert":
            {
                var command = UpsertCommand(
                    agent,
                    Binding(
                        "bind-beta",
                        AgentProfileSkillActivationMode.Routed,
                        ExactReference(SkillGuidBeta, "skill-beta")),
                    "op-identity-drift-upsert");
                await agent.HandleUpsertSkillBindingAsync(command);
                var candidate = command.Clone();
                candidate.Identity.OwningScopeId = "scope-other";
                replay = () => agent.HandleUpsertSkillBindingAsync(candidate);
                break;
            }
            case "remove":
            {
                var command = RemoveCommand(agent, "bind-alpha", "op-identity-drift-remove");
                await agent.HandleRemoveSkillBindingAsync(command);
                var candidate = command.Clone();
                candidate.Identity.OwningScopeId = "scope-other";
                replay = () => agent.HandleRemoveSkillBindingAsync(candidate);
                break;
            }
            case "publish":
            {
                var command = PublishCommand(
                    agent,
                    Snapshot(agent.State.Identity, agent.State.Draft),
                    "op-identity-drift-publish");
                await agent.HandlePublishAsync(command);
                var candidate = command.Clone();
                candidate.Identity.OwningScopeId = "scope-other";
                replay = () => agent.HandlePublishAsync(candidate);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        var eventCount = (await store.GetEventsAsync(agent.Id)).Count;
        var act = replay;

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task UpdateReplay_ShouldClassifyPayloadDriftBeforeSemanticPolicyRejection()
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var changed = agent.State.Draft.Clone();
        changed.DisplayName = "Changed";
        var command = UpdateCommand(
            agent.State.Identity,
            changed,
            "op-update-policy-drift",
            expectedVersion: 1);
        await agent.HandleUpdateDraftAsync(command);
        var eventCount = (await store.GetEventsAsync(agent.Id)).Count;
        var replay = command.Clone();
        replay.Content = ContentWithMultipleDefaultBindings();

        var act = () => agent.HandleUpdateDraftAsync(replay);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task Mutation_ShouldCommitTypedExpectedVersionRejection()
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var changed = GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Stale change");

        await agent.HandleUpdateDraftAsync(UpdateCommand(
            agent.State.Identity,
            changed,
            "op-update-stale",
            expectedVersion: 0));

        agent.State.DraftRevision.Should().Be(1);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("DRAFT_VERSION_CONFLICT");
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task IdenticalDraftMutation_ShouldCommitNoChangeWithoutAdvancingDraftRevision()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();

        await agent.HandleUpdateDraftAsync(UpdateCommand(
            agent.State.Identity,
            agent.State.Draft,
            "op-update-no-change",
            expectedVersion: 1));

        agent.State.DraftRevision.Should().Be(1);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.NoChange);
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public async Task UpsertBinding_ShouldKeepDeterministicOrderAndTreatIdenticalBindingAsNoChange()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();
        var zeta = Binding("bind-zeta", AgentProfileSkillActivationMode.Routed, ExactReference(SkillGuidBeta, "skill-zeta"));
        var alpha = Binding("bind-alpha", AgentProfileSkillActivationMode.Always, ExactReference(SkillGuidAlpha, "skill-alpha"));

        await agent.HandleUpsertSkillBindingAsync(UpsertCommand(agent, zeta, "op-upsert-zeta"));
        await agent.HandleUpsertSkillBindingAsync(UpsertCommand(agent, alpha, "op-upsert-alpha"));
        var draftRevision = agent.State.DraftRevision;
        await agent.HandleUpsertSkillBindingAsync(UpsertCommand(agent, alpha, "op-upsert-alpha-no-change"));

        agent.State.Draft.SkillBindings.Select(static binding => binding.BindingId)
            .Should().Equal("bind-alpha", "bind-zeta");
        agent.State.DraftRevision.Should().Be(draftRevision);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.NoChange);
    }

    [Fact]
    public async Task UpsertBinding_ShouldRejectMoreThanOneDefaultBinding()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, _, _) = await CreateInitializedActorAsync(content: content);
        var second = Binding(
            "bind-beta",
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            ExactReference(SkillGuidBeta, "skill-beta"));

        await agent.HandleUpsertSkillBindingAsync(UpsertCommand(agent, second, "op-upsert-second-default"));

        agent.State.Draft.SkillBindings.Should().ContainSingle();
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("MULTIPLE_DEFAULT_SKILLS");
    }

    [Fact]
    public async Task RemoveBinding_ShouldCommitTypedRejectionWhenBindingIsMissing()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();

        await agent.HandleRemoveSkillBindingAsync(RemoveCommand(
            agent,
            "bind-missing",
            "op-remove-missing"));

        agent.State.DraftRevision.Should().Be(1);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PROFILE_BINDING_CONFLICT");
    }

    [Fact]
    public async Task Publish_ShouldRejectDraftRaceBeforeAcceptingSealedSnapshot()
    {
        var (agent, _, publisher) = await CreateInitializedActorAsync();
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        var command = PublishCommand(agent, snapshot, "op-publish-stale-draft");
        command.ExpectedDraftRevision = 0;

        await agent.HandlePublishAsync(command);

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PUBLISH_SOURCE_CHANGED");
        publisher.Sends.Should().ContainSingle(); // initialization only
    }

    [Fact]
    public async Task Publish_ShouldRejectInvalidSnapshotDigest()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        snapshot.SnapshotSha256 = Digest(0x7f);

        await agent.HandlePublishAsync(PublishCommand(agent, snapshot, "op-publish-bad-snapshot"));

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PUBLISHED_SNAPSHOT_SHA256_MISMATCH");
    }

    [Fact]
    public async Task Publish_ShouldRejectInvalidSealedSkillDigest()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, _, _) = await CreateInitializedActorAsync(content: content);
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        var command = PublishCommand(agent, snapshot, "op-publish-bad-sealed");
        command.Snapshot.SkillBindings[0].Skill.ContentSha256 = Digest(0x7e);

        await agent.HandlePublishAsync(command);

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Diagnostic.Code.Should().Be("SEALED_SKILL_CONTENT_SHA256_MISMATCH");
    }

    [Theory]
    [InlineData("prompt", "AGGREGATE_PROMPT_BYTES_EXCEEDED")]
    [InlineData("asset", "TEXT_ASSET_TOO_LARGE")]
    [InlineData("skill", "SEALED_SKILL_TOO_LARGE")]
    [InlineData("snapshot", "PUBLISHED_SNAPSHOT_TOO_LARGE")]
    public async Task Publish_ShouldEnforceSharedHardLimitsDirectly(
        string boundary,
        string expectedCode)
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        var bindingCount = boundary == "snapshot" ? 17 : 1;
        content.SkillBindings.Add(Enumerable.Range(1, bindingCount).Select(index => Binding(
            $"bind-{index:D2}",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha"))));
        var (agent, store, _) = await CreateInitializedActorAsync(content: content);
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);

        switch (boundary)
        {
            case "prompt":
                snapshot.SkillBindings[0].Skill.Package.Instructions = new string('a', 65_537);
                break;
            case "asset":
                snapshot.SkillBindings[0].Skill.Package.Assets.Add(new AgentProfileNamedTextAsset
                {
                    Path = "large.txt",
                    Content = new string('a', 262_145),
                });
                break;
            case "skill":
                for (var index = 0; index < 4; index++)
                {
                    snapshot.SkillBindings[0].Skill.Package.Assets.Add(new AgentProfileNamedTextAsset
                    {
                        Path = $"asset-{index}.txt",
                        Content = new string((char)('a' + index), 262_144),
                    });
                }
                break;
            case "snapshot":
                foreach (var sealedBinding in snapshot.SkillBindings)
                {
                    sealedBinding.Skill.Package.Assets.Add(new AgentProfileNamedTextAsset
                    {
                        Path = "asset.txt",
                        Content = new string('a', 250_000),
                    });
                }
                break;
        }

        foreach (var sealedBinding in snapshot.SkillBindings)
        {
            sealedBinding.Skill.ContentSha256 =
                AgentProfileDeterminism.ComputeSealedSkillSha256(sealedBinding.Skill);
        }
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        var command = PublishCommand(agent, snapshot, $"op-publish-hard-limit-{boundary}");

        await agent.HandlePublishAsync(command);

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be(expectedCode);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task PublishHardLimitRejection_ShouldPersistBoundedDiagnosticFields()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, store, _) = await CreateInitializedActorAsync(content: content);
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        snapshot.SkillBindings[0].Skill.Package.Assets.Add(new AgentProfileNamedTextAsset
        {
            Path = new string('\u00e9', 600),
            Content = new string('a', AgentProfileValidationLimits.TextAssetMaxUtf8Bytes + 1),
        });
        snapshot.SkillBindings[0].Skill.ContentSha256 =
            AgentProfileDeterminism.ComputeSealedSkillSha256(snapshot.SkillBindings[0].Skill);
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);

        await agent.HandlePublishAsync(PublishCommand(
            agent,
            snapshot,
            "op-publish-bounded-hard-limit-diagnostic"));

        var diagnostic = agent.State.LastMutation.Diagnostic;
        Encoding.UTF8.GetByteCount(diagnostic.Code).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Message).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Path).Should().BeLessThanOrEqualTo(512);
        var rejection = (await store.GetEventsAsync(agent.Id))[^1].EventData
            .Unpack<AgentProfileMutationRejectedEvent>();
        rejection.Outcome.Diagnostic.Should().Be(diagnostic);
    }

    [Fact]
    public async Task Publish_ShouldValidateAuthoritativeRevisionWithinSnapshotLimit()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Enumerable.Range(1, 17).Select(index => Binding(
            $"bind-{index:D2}",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha"))));
        var (agent, store, _) = await CreateInitializedActorAsync(content: content);
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        for (var index = 0; index < 16; index++)
        {
            snapshot.SkillBindings[index].Skill.Package.Assets.Add(new AgentProfileNamedTextAsset
            {
                Path = "asset.txt",
                Content = new string((char)('a' + index), 250_000),
            });
        }
        var padding = new AgentProfileNamedTextAsset { Path = "padding.txt" };
        snapshot.SkillBindings[^1].Skill.Package.Assets.Add(padding);
        FitSnapshotToSerializedSize(
            snapshot,
            padding,
            AgentProfileValidationLimits.PublishedSnapshotMaxSerializedBytes);
        snapshot.CalculateSize().Should().Be(
            AgentProfileValidationLimits.PublishedSnapshotMaxSerializedBytes);

        await agent.HandlePublishAsync(PublishCommand(
            agent,
            snapshot,
            "op-publish-authoritative-revision-size"));

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PUBLISHED_SNAPSHOT_TOO_LARGE");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Publish_ShouldRequireExactDraftBindingMatch()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, _, _) = await CreateInitializedActorAsync(content: content);
        var otherContent = content.Clone();
        otherContent.SkillBindings.Clear();
        otherContent.SkillBindings.Add(Binding(
            "bind-beta",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidBeta, "skill-beta")));
        var snapshot = Snapshot(agent.State.Identity, otherContent);
        snapshot.SourceDraftSha256 = agent.State.DraftSha256;
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);

        await agent.HandlePublishAsync(PublishCommand(agent, snapshot, "op-publish-binding-mismatch"));

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PROFILE_BINDING_CONFLICT");
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("scope")]
    [InlineData("reference")]
    public async Task Publish_ShouldRejectSnapshotIdentityOutsideAuthoritativeBoundary(string boundary)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        switch (boundary)
        {
            case "profile":
                snapshot.Identity.ProfileId = "prof-other";
                break;
            case "owner":
                snapshot.Identity.Owner.User.SubjectId = "owner-other";
                break;
            case "scope":
                snapshot.Identity.OwningScopeId = "scope-other";
                break;
            case "reference":
                snapshot.Identity.Reference.ProfileSlug = "other-assistant";
                break;
        }
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        var command = PublishCommand(agent, snapshot, $"op-publish-identity-{boundary}");

        await agent.HandlePublishAsync(command);

        agent.State.Published.Should().BeNull();
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Rejected);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("PROFILE_IDENTITY_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FirstPublish_ShouldUseRevisionOneAndReplayShouldHealCatalogSendWithoutNewEvent()
    {
        var (agent, store, publisher) = await CreateInitializedActorAsync();
        var command = PublishCommand(
            agent,
            Snapshot(agent.State.Identity, agent.State.Draft),
            "op-publish-first");

        await agent.HandlePublishAsync(command);
        var eventCount = (await store.GetEventsAsync(agent.Id)).Count;
        var replay = command.Clone();
        replay.ExpectedAuthorityStateVersion = 0;
        replay.Operation.CommandId = "cmd-publish-retry";
        replay.Operation.CorrelationId = "corr-publish-retry";
        await agent.HandlePublishAsync(replay);

        agent.State.PublishedRevision.Should().Be(1);
        agent.State.Published.PublishedRevision.Should().Be(1);
        command.Snapshot.PublishedRevision.Should().Be(0);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(eventCount);
        publisher.Sends.Should().HaveCount(3); // initialize, publish, healing replay
        var summaries = publisher.Sends.Skip(1)
            .Select(static send => send.Payload.Unpack<ObserveAgentProfilePublishedSummaryCommand>())
            .ToArray();
        summaries.Should().OnlyContain(summary => summary.Summary.PublishedRevision == 1);
        summaries.Should().OnlyContain(summary => summary.Summary.Reference.Equals(agent.State.Identity.Reference));
        summaries[1].Operation.CommandId.Should().Be("cmd-publish-retry");
    }

    [Fact]
    public async Task PublishNoChange_ShouldRequireBothSourceAndExecutionDigestsToMatch()
    {
        var (agent, _, publisher) = await CreateInitializedActorAsync();
        var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
        await agent.HandlePublishAsync(PublishCommand(agent, snapshot, "op-publish-applied"));

        await agent.HandlePublishAsync(PublishCommand(
            agent,
            Snapshot(agent.State.Identity, agent.State.Draft),
            "op-publish-no-change"));

        agent.State.PublishedRevision.Should().Be(1);
        agent.State.LastMutation.Status.Should().Be(AgentProfileMutationStatus.NoChange);
        publisher.Sends.Should().HaveCount(3);
    }

    [Fact]
    public async Task Publish_ShouldIncrementRevisionWhenOnlySourceDraftDigestChanges()
    {
        var (agent, _, _) = await CreateInitializedActorAsync();
        var first = Snapshot(agent.State.Identity, agent.State.Draft);
        await agent.HandlePublishAsync(PublishCommand(agent, first, "op-publish-source-one"));
        var changed = agent.State.Draft.Clone();
        changed.DisplayName = "Display-only change";
        await agent.HandleUpdateDraftAsync(UpdateCommand(
            agent.State.Identity,
            changed,
            "op-display-only-update",
            expectedVersion: 2));
        var second = Snapshot(agent.State.Identity, agent.State.Draft);
        second.SnapshotSha256.Should().Equal(first.SnapshotSha256);
        second.SourceDraftSha256.Should().NotEqual(first.SourceDraftSha256);

        await agent.HandlePublishAsync(PublishCommand(agent, second, "op-publish-source-two"));

        agent.State.PublishedRevision.Should().Be(2);
        agent.State.Published.DisplayName.Should().Be("Display-only change");
    }

    [Fact]
    public async Task Publish_ShouldIncrementRevisionWhenOnlySealedExecutionDigestChanges()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        var (agent, _, _) = await CreateInitializedActorAsync(content: content);
        var first = Snapshot(agent.State.Identity, agent.State.Draft, packageVariant: "one");
        await agent.HandlePublishAsync(PublishCommand(agent, first, "op-publish-sealed-one"));
        var second = Snapshot(agent.State.Identity, agent.State.Draft, packageVariant: "two");
        second.SourceDraftSha256.Should().Equal(first.SourceDraftSha256);
        second.SnapshotSha256.Should().NotEqual(first.SnapshotSha256);

        await agent.HandlePublishAsync(PublishCommand(agent, second, "op-publish-sealed-two"));

        agent.State.PublishedRevision.Should().Be(2);
        agent.State.Published.SnapshotSha256.Should().Equal(second.SnapshotSha256);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("upsert")]
    [InlineData("remove")]
    [InlineData("publish")]
    public async Task CanonicalMutationBadDigest_ExactReplayShouldIgnoreCallerDigest(
        string mutation)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var command = CanonicalMutationCommand(agent, mutation, "alpha", $"op-bad-digest-{mutation}");
        var authoritativeDigest = CanonicalMutationInputSha256(command);
        command = CloneMutation(command);
        MutationOperation(command).InputSha256 = Digest(0x81);
        authoritativeDigest.Should().NotEqual(MutationOperation(command).InputSha256);
        await DispatchMutationAsync(agent, command);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        var replay = CloneMutation(command);
        MutationOperation(replay).InputSha256 = Digest(0x82);
        MutationOperation(replay).CommandId = $"cmd-bad-digest-{mutation}-retry";
        MutationOperation(replay).CorrelationId = $"corr-bad-digest-{mutation}-retry";

        await DispatchMutationAsync(agent, replay);

        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
        agent.State.LastMutation.Diagnostic.Code.Should().Be("OPERATION_INPUT_SHA256_MISMATCH");
    }

    [Theory]
    [InlineData("update")]
    [InlineData("upsert")]
    [InlineData("remove")]
    [InlineData("publish")]
    public async Task CanonicalMutationDigestAlias_ShouldNotReplayDifferentPayload(
        string mutation)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var operationId = $"op-digest-alias-{mutation}";
        var first = CanonicalMutationCommand(agent, mutation, "alpha", operationId);
        var second = CanonicalMutationCommand(agent, mutation, "beta", operationId);
        var secondDigest = CanonicalMutationInputSha256(second);
        CanonicalMutationInputSha256(first).Should().NotEqual(secondDigest);
        MutationOperation(first).InputSha256 = secondDigest;
        await DispatchMutationAsync(agent, first);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;

        var act = () => DispatchMutationAsync(agent, second);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Theory]
    [InlineData("update", "upsert")]
    [InlineData("upsert", "remove")]
    [InlineData("remove", "publish")]
    [InlineData("publish", "update")]
    public async Task MutationOperationIdReuseAcrossCommandFamilies_ShouldConflict(
        string firstMutation,
        string secondMutation)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var operationId = $"op-cross-family-{firstMutation}-{secondMutation}";
        var first = CanonicalMutationCommand(agent, firstMutation, "alpha", operationId);
        var second = CanonicalMutationCommand(agent, secondMutation, "beta", operationId);
        MutationOperation(first).InputSha256 = CanonicalMutationInputSha256(second);
        await DispatchMutationAsync(agent, first);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;

        var act = () => DispatchMutationAsync(agent, second);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task InitializeBadDigest_ExactReplayShouldIgnoreCallerDigestAndRejectPayloadAlias()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var identity = GAgentServiceTestKit.CreateAgentProfileIdentity();
        var firstContent = GAgentServiceTestKit.CreateAgentProfileContent(displayName: "First");
        var secondContent = GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Second");
        var command = InitializeCommand(identity, firstContent, "op-initialize-bad-digest-authority");
        command.Operation.InputSha256 =
            AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, secondContent);
        await DispatchInitializeAsync(agent, command);
        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        var replay = command.Clone();
        replay.Operation.InputSha256 = Digest(0x83);

        await DispatchInitializeAsync(agent, replay);

        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        publisher.Sends.Should().HaveCount(2);
        var drifted = InitializeCommand(identity, secondContent, command.Operation.OperationId);
        var act = () => DispatchInitializeAsync(agent, drifted);
        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task InitializeOperationIdReuseForMutation_ShouldConflict()
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var update = UpdateCommand(
            agent.State.Identity,
            GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Changed"),
            "op-initialize-update-cross-family",
            agent.EventSourcing!.CurrentVersion);
        var initialize = InitializeCommand(
            agent.State.Identity,
            agent.State.Draft,
            update.Operation.OperationId);
        initialize.Operation.InputSha256 = update.Operation.InputSha256;
        await DispatchInitializeAsync(agent, initialize);
        var rejectedVersion = agent.EventSourcing.CurrentVersion;

        var act = () => agent.HandleUpdateDraftAsync(update);

        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Fact]
    public async Task MissingInitializeContent_ShouldCommitReplayAndDistinguishPresentEmpty()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand(operationId: "op-missing-initialize-content");
        command.InitialContent = null!;
        command.Operation.InputSha256 = Digest(0x84);

        await DispatchInitializeAsync(agent, command);

        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        agent.State.Operations.Single().InitializationRejection.Continuation.Diagnostic.Code
            .Should().Be("MISSING_PROFILE_CONTENT");
        var replay = command.Clone();
        replay.Operation.InputSha256 = Digest(0x85);
        await DispatchInitializeAsync(agent, replay);
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        publisher.Sends.Should().HaveCount(2);
        var drifted = command.Clone();
        drifted.InitialContent = new AgentProfileContent();
        var act = () => DispatchInitializeAsync(agent, drifted);
        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    [Theory]
    [InlineData("update", "MISSING_PROFILE_CONTENT")]
    [InlineData("upsert", "MISSING_SKILL_BINDING")]
    [InlineData("publish", "MISSING_PUBLISHED_SNAPSHOT")]
    public async Task MissingMutationPayload_ShouldCommitReplayAndDistinguishPresentEmpty(
        string mutation,
        string expectedCode)
    {
        var (agent, store, _) = await CreateInitializedActorAsync();
        var command = CanonicalMutationCommand(
            agent,
            mutation,
            "missing",
            $"op-missing-{mutation}-payload");
        ClearNestedMutationPayload(command);
        MutationOperation(command).InputSha256 = Digest(0x86);

        await DispatchMutationAsync(agent, command);

        var rejectedVersion = agent.EventSourcing!.CurrentVersion;
        agent.State.LastMutation.Diagnostic.Code.Should().Be(expectedCode);
        var replay = CloneMutation(command);
        MutationOperation(replay).InputSha256 = Digest(0x87);
        await DispatchMutationAsync(agent, replay);
        agent.EventSourcing.CurrentVersion.Should().Be(rejectedVersion);
        var drifted = CloneMutation(command);
        SetPresentEmptyNestedMutationPayload(drifted);
        var act = () => DispatchMutationAsync(agent, drifted);
        var exception = await act.Should().ThrowAsync<AgentProfileActorInvariantException>();
        exception.Which.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount((int)rejectedVersion);
    }

    private static async Task<(AgentProfileGAgent Agent, InMemoryEventStore Store, RecordingProfileEventPublisher Publisher)>
        CreateActorAsync()
    {
        var store = new InMemoryEventStore();
        var publisher = new RecordingProfileEventPublisher();
        var identity = GAgentServiceTestKit.CreateAgentProfileIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<AgentProfileGAgent, AgentProfileState>(
            store,
            AgentProfileActorIds.Profile(identity.ProfileId),
            static () => new AgentProfileGAgent());
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        return (agent, store, publisher);
    }

    private static async Task<(AgentProfileGAgent Agent, InMemoryEventStore Store, RecordingProfileEventPublisher Publisher)>
        CreateInitializedActorAsync(
            AgentProfileIdentity? identity = null,
            AgentProfileContent? content = null)
    {
        var fixture = await CreateActorAsync();
        await DispatchInitializeAsync(fixture.Agent, InitializeCommand(identity, content));
        return fixture;
    }

    private static Task DispatchInitializeAsync(
        AgentProfileGAgent agent,
        InitializeAgentProfileCommand command,
        string publisherActorId = AgentProfileActorIds.Namespace) =>
        GAgentServiceTestKit.DispatchAsync(agent, command, publisherActorId);

    private static IMessage CanonicalMutationCommand(
        AgentProfileGAgent agent,
        string mutation,
        string variant,
        string operationId)
    {
        if (mutation == "publish")
        {
            var snapshot = Snapshot(agent.State.Identity, agent.State.Draft);
            snapshot.Purpose = $"Purpose {variant}";
            snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
            return PublishCommand(agent, snapshot, operationId);
        }

        return mutation switch
        {
            "update" => UpdateCommand(
                agent.State.Identity,
                GAgentServiceTestKit.CreateAgentProfileContent(displayName: $"Changed {variant}"),
                operationId,
                agent.EventSourcing!.CurrentVersion),
            "upsert" => UpsertCommand(
                agent,
                Binding(
                    $"bind-{variant}",
                    AgentProfileSkillActivationMode.Routed,
                    ExactReference(SkillGuidAlpha, "skill-alpha")),
                operationId),
            "remove" => RemoveCommand(agent, $"bind-{variant}", operationId),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
    }

    private static ByteString CanonicalMutationInputSha256(IMessage command) =>
        command switch
        {
            UpdateAgentProfileDraftCommand update =>
                AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                    update.Identity,
                    update.Content),
            UpsertAgentProfileSkillBindingCommand upsert =>
                AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
                    upsert.Identity,
                    upsert.Binding),
            RemoveAgentProfileSkillBindingCommand remove =>
                AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                    remove.Identity,
                    remove.BindingId),
            PublishAgentProfileCommand publish =>
                AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                    publish.Identity,
                    publish.Snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static void ClearNestedMutationPayload(IMessage command)
    {
        switch (command)
        {
            case UpdateAgentProfileDraftCommand update:
                update.Content = null!;
                break;
            case UpsertAgentProfileSkillBindingCommand upsert:
                upsert.Binding = null!;
                break;
            case PublishAgentProfileCommand publish:
                publish.Snapshot = null!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void SetPresentEmptyNestedMutationPayload(IMessage command)
    {
        switch (command)
        {
            case UpdateAgentProfileDraftCommand update:
                update.Content = new AgentProfileContent();
                break;
            case UpsertAgentProfileSkillBindingCommand upsert:
                upsert.Binding = new AgentProfileSkillBinding();
                break;
            case PublishAgentProfileCommand publish:
                publish.Snapshot = new AgentProfilePublishedSnapshot();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void FitSnapshotToSerializedSize(
        AgentProfilePublishedSnapshot snapshot,
        AgentProfileNamedTextAsset padding,
        int targetSize)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            foreach (var binding in snapshot.SkillBindings)
            {
                binding.Skill.ContentSha256 =
                    AgentProfileDeterminism.ComputeSealedSkillSha256(binding.Skill);
            }
            snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
            var delta = targetSize - snapshot.CalculateSize();
            if (delta == 0)
                return;
            var nextLength = checked(padding.Content.Length + delta);
            if (nextLength < 0 || nextLength > AgentProfileValidationLimits.TextAssetMaxUtf8Bytes)
                throw new InvalidOperationException("The snapshot boundary fixture cannot satisfy shared limits.");
            padding.Content = new string('z', nextLength);
        }

        throw new InvalidOperationException("The snapshot boundary fixture did not converge.");
    }

    private static IMessage MalformedMutationCommand(
        AgentProfileGAgent agent,
        string mutation,
        string boundary,
        string operationId)
    {
        IMessage command = mutation switch
        {
            "update" => UpdateCommand(
                agent.State.Identity,
                GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Changed"),
                operationId,
                agent.EventSourcing!.CurrentVersion),
            "upsert" => UpsertCommand(
                agent,
                Binding(
                    "bind-alpha",
                    AgentProfileSkillActivationMode.Routed,
                    ExactReference(SkillGuidAlpha, "skill-alpha")),
                operationId),
            "remove" => RemoveCommand(agent, "bind-alpha", operationId),
            "publish" => PublishCommand(
                agent,
                Snapshot(agent.State.Identity, agent.State.Draft),
                operationId),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
        InvalidateMutation(command, boundary, drifted: false);
        return command;
    }

    private static void InvalidateMutation(IMessage command, string boundary, bool drifted)
    {
        if (boundary == "identity")
        {
            MutationIdentity(command).Reference.ProfileSlug = drifted ? "ALSO-INVALID" : "INVALID";
            return;
        }

        switch (command)
        {
            case UpdateAgentProfileDraftCommand update:
                update.Content.DisplayName = drifted ? new string('u', 257) : string.Empty;
                break;
            case UpsertAgentProfileSkillBindingCommand upsert:
                upsert.Binding.BindingId = drifted ? new string('u', 129) : string.Empty;
                break;
            case RemoveAgentProfileSkillBindingCommand remove:
                remove.BindingId = drifted ? new string('r', 129) : string.Empty;
                break;
            case PublishAgentProfileCommand publish:
                publish.Snapshot.DisplayName = drifted ? new string('p', 257) : string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static IMessage CloneMutation(IMessage command) =>
        command switch
        {
            UpdateAgentProfileDraftCommand update => update.Clone(),
            UpsertAgentProfileSkillBindingCommand upsert => upsert.Clone(),
            RemoveAgentProfileSkillBindingCommand remove => remove.Clone(),
            PublishAgentProfileCommand publish => publish.Clone(),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static AgentProfileIdentity MutationIdentity(IMessage command) =>
        command switch
        {
            UpdateAgentProfileDraftCommand update => update.Identity,
            UpsertAgentProfileSkillBindingCommand upsert => upsert.Identity,
            RemoveAgentProfileSkillBindingCommand remove => remove.Identity,
            PublishAgentProfileCommand publish => publish.Identity,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static AgentProfileOperationFact MutationOperation(IMessage command) =>
        command switch
        {
            UpdateAgentProfileDraftCommand update => update.Operation,
            UpsertAgentProfileSkillBindingCommand upsert => upsert.Operation,
            RemoveAgentProfileSkillBindingCommand remove => remove.Operation,
            PublishAgentProfileCommand publish => publish.Operation,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static Task DispatchMutationAsync(AgentProfileGAgent agent, IMessage command) =>
        command switch
        {
            UpdateAgentProfileDraftCommand update => agent.HandleUpdateDraftAsync(update),
            UpsertAgentProfileSkillBindingCommand upsert => agent.HandleUpsertSkillBindingAsync(upsert),
            RemoveAgentProfileSkillBindingCommand remove => agent.HandleRemoveSkillBindingAsync(remove),
            PublishAgentProfileCommand publish => agent.HandlePublishAsync(publish),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static InitializeAgentProfileCommand InitializeCommand(
        AgentProfileIdentity? identity = null,
        AgentProfileContent? content = null,
        string operationId = "op-initialize-alpha")
    {
        identity ??= GAgentServiceTestKit.CreateAgentProfileIdentity();
        content ??= GAgentServiceTestKit.CreateAgentProfileContent();
        return new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            InitialContent = content.Clone(),
            NamespaceActorId = AgentProfileActorIds.Namespace,
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(identity, content)),
        };
    }

    private static UpdateAgentProfileDraftCommand UpdateCommand(
        AgentProfileIdentity identity,
        AgentProfileContent content,
        string operationId,
        long expectedVersion) =>
        new()
        {
            Identity = identity.Clone(),
            Content = content.Clone(),
            ExpectedAuthorityStateVersion = expectedVersion,
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(identity, content)),
        };

    private static UpsertAgentProfileSkillBindingCommand UpsertCommand(
        AgentProfileGAgent agent,
        AgentProfileSkillBinding binding,
        string operationId) =>
        new()
        {
            Identity = agent.State.Identity.Clone(),
            Binding = binding.Clone(),
            ExpectedAuthorityStateVersion = agent.EventSourcing!.CurrentVersion,
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
                    agent.State.Identity,
                    binding)),
        };

    private static RemoveAgentProfileSkillBindingCommand RemoveCommand(
        AgentProfileGAgent agent,
        string bindingId,
        string operationId) =>
        new()
        {
            Identity = agent.State.Identity.Clone(),
            BindingId = bindingId,
            ExpectedAuthorityStateVersion = agent.EventSourcing!.CurrentVersion,
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                    agent.State.Identity,
                    bindingId)),
        };

    private static PublishAgentProfileCommand PublishCommand(
        AgentProfileGAgent agent,
        AgentProfilePublishedSnapshot snapshot,
        string operationId) =>
        new()
        {
            Identity = agent.State.Identity.Clone(),
            ExpectedAuthorityStateVersion = agent.EventSourcing!.CurrentVersion,
            ExpectedDraftRevision = agent.State.DraftRevision,
            ExpectedDraftSha256 = agent.State.DraftSha256,
            Snapshot = snapshot.Clone(),
            Operation = GAgentServiceTestKit.CreateAgentProfileOperation(
                operationId,
                AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                    agent.State.Identity,
                    snapshot)),
        };

    private static AgentProfilePublishedSnapshot Snapshot(
        AgentProfileIdentity identity,
        AgentProfileContent content,
        string packageVariant = "one")
    {
        var normalized = AgentProfileDeterminism.NormalizeContent(content);
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = AgentProfileDeterminism.NormalizeIdentity(identity),
            DisplayName = normalized.DisplayName,
            Purpose = normalized.Purpose,
            Instructions = normalized.Instructions,
            ToolPolicy = normalized.ToolPolicy.Clone(),
            SourceDraftSha256 = AgentProfileDeterminism.ComputeSourceDraftSha256(normalized),
        };
        snapshot.SkillBindings.Add(normalized.SkillBindings.Select(binding =>
            SealedBinding(binding, packageVariant)));
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        return snapshot;
    }

    private static AgentProfileContent ContentWithMultipleDefaultBindings()
    {
        var content = GAgentServiceTestKit.CreateAgentProfileContent();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            ExactReference(SkillGuidAlpha, "skill-alpha")));
        content.SkillBindings.Add(Binding(
            "bind-beta",
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            ExactReference(SkillGuidBeta, "skill-beta")));
        return content;
    }

    private static SealedAgentProfileSkillBinding SealedBinding(
        AgentProfileSkillBinding binding,
        string packageVariant)
    {
        var skill = new SealedAgentProfileSkill
        {
            ExactReference = binding.Skill.Clone(),
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = binding.Skill.SkillGuid,
                LiteralVersion = binding.Skill.LiteralVersion,
                CanonicalName = binding.Skill.ExpectedName,
                PublisherId = binding.Skill.ExpectedPublisherId,
                UpstreamSkillHash = $"upstream-{packageVariant}",
                Description = $"Description {packageVariant}",
                Instructions = $"Instructions {packageVariant}",
                ModelInvocable = true,
                UserInvocable = true,
            },
        };
        skill.ContentSha256 = AgentProfileDeterminism.ComputeSealedSkillSha256(skill);
        return new SealedAgentProfileSkillBinding
        {
            BindingId = binding.BindingId,
            ActivationMode = binding.ActivationMode,
            Skill = skill,
        };
    }

    private static AgentProfileSkillBinding Binding(
        string bindingId,
        AgentProfileSkillActivationMode activationMode,
        ExactOrnnSkillReference reference) =>
        new()
        {
            BindingId = bindingId,
            ActivationMode = activationMode,
            Skill = reference.Clone(),
        };

    private static ExactOrnnSkillReference ExactReference(string guid, string name) =>
        new()
        {
            SkillGuid = guid,
            LiteralVersion = "1.0",
            ExpectedName = name,
            ExpectedPublisherId = "publisher-alpha",
        };

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());
}
