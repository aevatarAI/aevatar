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

        await agent.HandleInitializeAsync(command);
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
    public async Task Initialize_IdenticalOperationReplayShouldResendContinuationWithoutNewEvent()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        await agent.HandleInitializeAsync(command);
        var replay = command.Clone();
        replay.Operation.CommandId = "cmd-initialize-retry";
        replay.Operation.CorrelationId = "corr-initialize-retry";

        await agent.HandleInitializeAsync(replay);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().HaveCount(2);
        publisher.Sends[1].Payload.Unpack<AgentProfileInitializedContinuation>()
            .Operation.CommandId.Should().Be("cmd-initialize-retry");
    }

    [Fact]
    public async Task Initialize_ShouldRejectPayloadDriftOrImmutableIdentityChangeWithoutMutatingState()
    {
        var (agent, store, publisher) = await CreateActorAsync();
        var command = InitializeCommand();
        await agent.HandleInitializeAsync(command);
        var drifted = InitializeCommand(
            content: GAgentServiceTestKit.CreateAgentProfileContent(displayName: "Drifted"));
        var otherIdentity = GAgentServiceTestKit.CreateAgentProfileIdentity(profileId: "prof-other");
        var changedIdentity = InitializeCommand(otherIdentity, operationId: "op-initialize-other");

        await agent.HandleInitializeAsync(drifted);
        await agent.HandleInitializeAsync(changedIdentity);

        (await store.GetEventsAsync(agent.Id)).Should().ContainSingle();
        publisher.Sends.Should().HaveCount(3);
        publisher.Sends[1].Payload.Unpack<AgentProfileInitializationRejectedContinuation>()
            .Diagnostic.Code.Should().Be("IDEMPOTENCY_PAYLOAD_CONFLICT");
        publisher.Sends[2].Payload.Unpack<AgentProfileInitializationRejectedContinuation>()
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
        await fixture.Agent.HandleInitializeAsync(InitializeCommand(identity, content));
        return fixture;
    }

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
