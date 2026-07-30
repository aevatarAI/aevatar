using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
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
        await actor.HandleInitializeAsync(new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            NamespaceActorId = "namespace-alpha",
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
        AgentProfileDeterminism.VerifyPublishedSnapshot(actor.State.Published).Should().BeTrue();
    }

    [Fact]
    public async Task ReplayingOperation_ShouldNotAdvanceAuthorityState()
    {
        var store = new InMemoryEventStore();
        var actor = CreateActor(store);
        var command = new InitializeAgentProfileCommand
        {
            Identity = Identity(),
            NamespaceActorId = "namespace-alpha",
            Operation = Operation("op-init", "init"),
        };

        await actor.HandleInitializeAsync(command);
        await actor.HandleInitializeAsync(command.Clone());

        actor.State.Operations.Should().ContainSingle();
        (await store.GetEventsAsync("agent-profile-test")).Should().ContainSingle();
    }

    private static AgentProfileGAgent CreateActor(InMemoryEventStore? store = null) =>
        GAgentServiceTestKit.CreateStatefulAgent<AgentProfileGAgent, AgentProfileState>(
            store ?? new InMemoryEventStore(),
            "agent-profile-test",
            static () => new AgentProfileGAgent());

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
