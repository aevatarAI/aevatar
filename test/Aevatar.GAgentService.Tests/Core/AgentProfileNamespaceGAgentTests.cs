using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class AgentProfileNamespaceGAgentTests
{
    [Fact]
    public async Task CreateAndInitialize_ShouldActivateOneOpaqueProfileEntry()
    {
        var actor = CreateActor();
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var operation = Operation("op-create", "create");

        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            ProfileActorId = "opaque-profile-actor",
            Operation = operation.Clone(),
        });
        await actor.HandleInitializedAsync(new AgentProfileInitialized
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            Operation = operation.Clone(),
        });

        actor.State.Owner.Should().Be(owner);
        actor.State.Profiles.Should().ContainSingle(x =>
            x.ProfileId == "prof-alpha" && x.Status == AgentProfileProvisioningStatus.Active);
    }

    [Fact]
    public async Task DefaultBinding_ShouldRequirePublishedProfileInTheSameNamespace()
    {
        var actor = CreateActor();
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var create = Operation("op-create", "create");
        await actor.HandleCreateAsync(new CreateAgentProfileCommand
        {
            Owner = owner.Clone(),
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            ProfileActorId = "opaque-profile-actor",
            Operation = create.Clone(),
        });
        await actor.HandleInitializedAsync(new AgentProfileInitialized
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            Operation = create.Clone(),
        });

        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            ProfileId = "prof-alpha",
            Enabled = true,
            CohortBasisPoints = 10_000,
            ExpectedAuthorityStateVersion = 2,
            Operation = Operation("op-bind-before-publish", "bind-before-publish"),
        });

        actor.State.DefaultBindings.Should().BeEmpty();
        actor.State.LastMutation.Code.Should().Be("PROFILE_NOT_PUBLISHED");

        await actor.HandleObservePublishedAsync(new ObserveAgentProfilePublishedCommand
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = "prof-alpha",
                Owner = owner.Clone(),
                ProfileSlug = "research-assistant",
            },
            PublishedRevision = 1,
            SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 32).ToArray()),
            DisplayName = "Research Assistant",
            Purpose = "Research public sources",
        });
        await actor.HandleSetDefaultBindingAsync(new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            ProfileId = "prof-alpha",
            Enabled = true,
            CohortBasisPoints = 10_000,
            ExpectedAuthorityStateVersion = 4,
            Operation = Operation("op-bind", "bind"),
        });

        actor.State.DefaultBindings.Should().ContainSingle(x =>
            x.AgentKind == AgentProfilePolicies.NyxIdChatAgentKind && x.ProfileId == "prof-alpha");
    }

    private static AgentProfileNamespaceGAgent CreateActor() =>
        GAgentServiceTestKit.CreateStatefulAgent<AgentProfileNamespaceGAgent, AgentProfileNamespaceState>(
            new InMemoryEventStore(),
            "agent-profile-namespace-test",
            static () => new AgentProfileNamespaceGAgent());

    private static AgentProfileOperationFact Operation(string operationId, string input) => new()
    {
        OperationId = operationId,
        CommandId = $"cmd-{operationId}",
        CorrelationId = $"corr-{operationId}",
        InputSha256 = ByteString.CopyFrom(AgentProfileDeterminism.Sha256Utf8(input)),
        RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
    };
}
