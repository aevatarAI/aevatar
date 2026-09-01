using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Hosting.AgentProfiles;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Hosting;

public sealed class NyxIdChatSystemAgentProfileDraftFactoryTests
{
    [Fact]
    public void Create_ShouldBuildNyxIdChatDraftWithConfiguredWorkflowToolPolicy()
    {
        var options = new NyxIdChatSystemAgentProfileBootstrapOptions
        {
            ProfileSlug = "nyxid-chat-default",
            DisplayName = "NyxID Chat Default",
            Purpose = "Default public chat.",
            Instructions = "Collect dinner reservation details before starting the workflow.",
            PolicyRevision = "dinner-date-mock-v1",
            MaxOwnedToolCount = 64,
            MaxSchemaBytes = 262_144,
            MaximumToolPolicy = new AgentProfileToolPolicyOptions
            {
                ToolNames =
                {
                    "ask_user",
                    "aevatar_start_workflow",
                    "aevatar_observe_workflow_run",
                },
                ToolSetRefs = { AgentProfilePolicies.NyxIdChatRouteToolSet },
            },
            Members =
            {
                new AgentProfileSkillMemberOptions
                {
                    IntentId = "dinner-reservation",
                    RoutingDescription = "Dinner reservation workflow entrypoint.",
                    SkillGuid = "11111111-1111-1111-1111-111111111111",
                    LiteralVersion = "1.0",
                    ExpectedSkillName = "dinner-reservation",
                    ReviewedPublisherId = "aevatar",
                    ExplicitTriggerAliases = { "dinner" },
                    TaskToolPolicy = new AgentProfileToolPolicyOptions
                    {
                        ToolNames = { "ask_user", "aevatar_start_workflow" },
                    },
                },
            },
        };

        var draft = NyxIdChatSystemAgentProfileDraftFactory.Create(options);

        draft.DisplayName.Should().Be("NyxID Chat Default");
        draft.Instructions.Should().Be("Collect dinner reservation details before starting the workflow.");
        draft.RuntimeProfile.AgentKind.Should().Be(AgentProfilePolicies.NyxIdChatAgentKind);
        draft.RuntimeProfile.RouteToolSetRef.Should().Be(AgentProfilePolicies.NyxIdChatRouteToolSet);
        draft.RuntimeProfile.ActivationMode.Should().Be(AgentProfileActivationMode.Enforced);
        draft.RuntimeProfile.PolicyRevision.Should().Be("dinner-date-mock-v1");
        draft.RuntimeProfile.MaximumToolPolicy.ToolNames.Should().Contain("aevatar_start_workflow");
        draft.RuntimeProfile.MaximumToolPolicy.ToolSetRefs.Should().Contain(AgentProfilePolicies.NyxIdChatRouteToolSet);
        draft.RuntimeProfile.MaxOwnedToolCount.Should().Be(64);
        draft.RuntimeProfile.MaxSchemaBytes.Should().Be(262_144);
        draft.RuntimeProfile.Members.Should().ContainSingle();
        draft.RuntimeProfile.Members[0].SkillRef.Guid.Should().Be("11111111-1111-1111-1111-111111111111");
        draft.RuntimeProfile.Members[0].ExplicitTriggerAliases.Should().ContainSingle().Which.Should().Be("dinner");
        AgentProfilePolicies.ValidateDraft(draft).Should().BeEmpty();
    }
}
