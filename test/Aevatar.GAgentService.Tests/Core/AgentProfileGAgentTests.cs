using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class AgentProfileGAgentTests
{
    [Fact]
    public async Task DraftAndPublish_ShouldKeepIndependentRevisions_AndRejectStaleSource()
    {
        var actor = CreateActor();
        var identity = Identity();
        await InitializeAsync(actor, new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = Operation("op-init", "init"),
        });

        var draft = Draft("First instructions");
        await actor.HandleUpdateDraftAsync(new UpdateAgentProfileDraftCommand
        {
            Identity = identity.Clone(),
            Draft = draft.Clone(),
            ExpectedAuthorityStateVersion = 1,
            Operation = Operation("op-draft", "draft"),
        });

        actor.State.DraftRevision.Should().Be(1);
        actor.State.PublishedRevision.Should().Be(0);

        var staleSnapshot = AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            draft,
            draftRevision: 1,
            publishedRevision: 1,
            publishedAt: DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        await actor.HandlePublishAsync(new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = staleSnapshot,
            SourceDraftSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x7f, 32).ToArray()),
            ExpectedAuthorityStateVersion = 2,
            Operation = Operation("op-publish-stale", "publish-stale"),
        });

        actor.State.PublishedRevision.Should().Be(0);
        actor.State.LastMutation.Code.Should().Be("DRAFT_SOURCE_MISMATCH");

        var snapshot = AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            draft,
            draftRevision: 1,
            publishedRevision: 1,
            publishedAt: DateTimeOffset.Parse("2026-07-30T00:01:00Z"));
        await actor.HandlePublishAsync(new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = snapshot,
            SourceDraftSha256 = actor.State.DraftSha256,
            ExpectedAuthorityStateVersion = 3,
            Operation = Operation("op-publish", "publish"),
        });

        actor.State.DraftRevision.Should().Be(1);
        actor.State.PublishedRevision.Should().Be(1);
        actor.State.Published.RuntimeProfile.Instructions.Should().Be("First instructions");
        AgentProfileDeterminism.VerifyPublishedSnapshot(actor.State.Published, actor.State.Draft).Should().BeTrue();
    }

    [Fact]
    public async Task Publish_ShouldRejectSelfConsistentSnapshotBuiltFromDifferentDraft()
    {
        var actor = CreateActor();
        var identity = Identity();
        await InitializeAsync(actor, new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            InitialDraft = Draft("Authority instructions"),
            Operation = Operation("op-init-canonical", "init-canonical"),
        });
        var forged = AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            Draft("Forged instructions"),
            draftRevision: 1,
            publishedRevision: 1,
            publishedAt: DateTimeOffset.Parse("2026-07-30T00:00:00Z"));

        await actor.HandlePublishAsync(new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = forged,
            SourceDraftSha256 = actor.State.DraftSha256,
            ExpectedAuthorityStateVersion = 1,
            Operation = Operation("op-publish-forged", "publish-forged"),
        });

        actor.State.PublishedRevision.Should().Be(0);
        actor.State.LastMutation.Code.Should().Be("PUBLISHED_SNAPSHOT_INVALID");
    }

    [Fact]
    public async Task Publish_ShouldRejectTamperedInnerRuntimeDigest()
    {
        var actor = CreateActor();
        var identity = Identity();
        var draft = Draft("Authority instructions");
        await InitializeAsync(actor, new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            InitialDraft = draft.Clone(),
            Operation = Operation("op-init-digest", "init-digest"),
        });
        var snapshot = AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            draft,
            draftRevision: 1,
            publishedRevision: 1,
            publishedAt: DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        snapshot.RuntimeProfile.DeterministicPolicySha256 = ByteString.CopyFrom(new byte[32]);

        await actor.HandlePublishAsync(new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = snapshot,
            SourceDraftSha256 = actor.State.DraftSha256,
            ExpectedAuthorityStateVersion = 1,
            Operation = Operation("op-publish-digest", "publish-digest"),
        });

        actor.State.PublishedRevision.Should().Be(0);
        actor.State.LastMutation.Code.Should().Be("PUBLISHED_SNAPSHOT_INVALID");
    }

    [Fact]
    public async Task ReplayingOperation_ShouldNotAdvanceAuthorityState()
    {
        var store = new InMemoryEventStore();
        var actor = CreateActor(store);
        var command = new InitializeAgentProfileCommand
        {
            Identity = Identity(),
            Operation = Operation("op-init", "init"),
        };

        await InitializeAsync(actor, command);
        await InitializeAsync(actor, command.Clone());

        actor.State.Operations.Should().ContainSingle();
        (await store.GetEventsAsync(actor.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Initialize_ShouldRejectActorAddressOrNamespacePublisherMismatch()
    {
        var identity = Identity();
        var forgedActor = CreateActor(actorId: "forged-profile-actor");
        var command = new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = Operation("op-forged-actor", "forged-actor"),
        };

        var forgedAddress = () => forgedActor.HandleEventAsync(
            Envelope(command, AgentProfileActorIds.Namespace(identity.Owner), forgedActor.Id));

        await forgedAddress.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*address*");

        var actor = CreateActor();
        var forgedPublisher = () => actor.HandleEventAsync(
            Envelope(command, "forged-namespace-actor", actor.Id));

        await forgedPublisher.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*");
        actor.State.Identity.Should().BeNull();
    }

    [Fact]
    public async Task InitializeReplay_ShouldRejectSemanticDriftDespiteCallerDigestReuse()
    {
        var actor = CreateActor();
        var command = new InitializeAgentProfileCommand
        {
            Identity = Identity(),
            InitialDraft = Draft("Original instructions"),
            Operation = Operation("op-semantic-replay", "caller-digest"),
        };
        await InitializeAsync(actor, command);
        var drifted = command.Clone();
        drifted.InitialDraft = Draft("Changed instructions");

        var act = () => InitializeAsync(actor, drifted);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*payload drift*");
        actor.State.Draft.Instructions.Should().Be("Original instructions");
    }

    private static AgentProfileGAgent CreateActor(
        InMemoryEventStore? store = null,
        string? actorId = null) =>
        GAgentServiceTestKit.CreateStatefulAgent<AgentProfileGAgent, AgentProfileState>(
            store ?? new InMemoryEventStore(),
            actorId ?? AgentProfileActorIds.Profile(Identity().ProfileId),
            static () => new AgentProfileGAgent());

    private static Task InitializeAsync(AgentProfileGAgent actor, InitializeAgentProfileCommand command) =>
        actor.HandleEventAsync(Envelope(
            command,
            AgentProfileActorIds.Namespace(command.Identity.Owner),
            actor.Id));

    private static EventEnvelope Envelope(IMessage payload, string publisherActorId, string targetActorId) => new()
    {
        Id = $"test-{Guid.NewGuid():N}",
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
        Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
        Payload = Any.Pack(payload),
    };

    private static AgentProfileIdentity Identity() => new()
    {
        ProfileId = "prof-alpha",
        Owner = AgentProfileOwners.ForScope("scope-gamma"),
        ProfileSlug = "research-assistant",
    };

    private static AgentProfileDraft Draft(string instructions) => new()
    {
        DisplayName = "Research Assistant",
        Purpose = "Research public sources",
        Instructions = instructions,
        RuntimeProfile = new AgentProfileSnapshot
        {
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            ActivationMode = AgentProfileActivationMode.Enforced,
            MaxPlanSteps = 4,
            HandoffTtlSeconds = 900,
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1500,
            MaxSelectedSkillBytes = 24576,
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
        },
    };

    private static AgentProfileOperationFact Operation(string operationId, string input) => new()
    {
        OperationId = operationId,
        CommandId = $"cmd-{operationId}",
        CorrelationId = $"corr-{operationId}",
        InputSha256 = ByteString.CopyFrom(AgentProfileDeterminism.Sha256Utf8(input)),
        RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
    };
}
