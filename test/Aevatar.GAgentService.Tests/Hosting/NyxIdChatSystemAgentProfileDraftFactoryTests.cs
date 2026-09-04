using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Hosting.AgentProfiles;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Hosting;

public sealed class NyxIdChatSystemAgentProfileDraftFactoryTests
{
    [Fact]
    public void Create_WhenMembersAreEmpty_ShouldBuildDraftWithConfiguredWorkflowToolPolicy()
    {
        var options = new NyxIdChatSystemAgentProfileBootstrapOptions
        {
            ProfileSlug = "nyxid-chat-default",
            DisplayName = "NyxID Chat Default",
            Purpose = "Default public chat.",
            Instructions = "For dinner reservation requests, start workflow_id dinner_date with aevatar_start_workflow when required booking details are present.",
            PolicyRevision = "dinner-date-mock-v1",
            MaximumToolPolicy = new AgentProfileToolPolicyOptions
            {
                ToolNames =
                {
                    "ask_user",
                    "aevatar_start_workflow",
                    "aevatar_observe_run",
                },
                ToolSetRefs = { AgentProfilePolicies.NyxIdChatRouteToolSet },
                ConnectedServiceSelectors =
                {
                    new AgentProfileConnectedServiceSelectorOptions
                    {
                        EndpointId = "readDiningProfileContext",
                        AllowedRisks = { "read_only" },
                    },
                },
            },
        };

        var draft = NyxIdChatSystemAgentProfileDraftFactory.Create(options);

        draft.RuntimeProfile.MaximumToolPolicy.ToolNames.Should().Contain("aevatar_start_workflow");
        draft.RuntimeProfile.MaximumToolPolicy.ToolSetRefs.Should().Contain(AgentProfilePolicies.NyxIdChatRouteToolSet);
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Should().ContainSingle().Which
            .EndpointId.Should().Be("readDiningProfileContext");
        draft.RuntimeProfile.MaxPlanSteps.Should().Be(AgentProfileValidationLimits.RequiredMaxPlanSteps);
        draft.RuntimeProfile.MaxOwnedToolCount.Should().Be(AgentProfileValidationLimits.MaximumOwnedToolCount);
        draft.RuntimeProfile.MaxSchemaBytes.Should().Be(AgentProfileValidationLimits.MaximumSchemaBytes);
        draft.RuntimeProfile.Members.Should().BeEmpty();
        AgentProfilePolicies.ValidateDraft(draft).Should().BeEmpty();
    }

    [Fact]
    public void Create_ShouldBuildNyxIdChatDraftWithConfiguredWorkflowToolPolicy()
    {
        var options = new NyxIdChatSystemAgentProfileBootstrapOptions
        {
            ProfileSlug = "nyxid-chat-default",
            DisplayName = "NyxID Chat Default",
            Purpose = "Default public chat.",
            Instructions = "For dinner reservation requests, start workflow_id dinner_date with aevatar_start_workflow when required booking details are present.",
            PolicyRevision = "dinner-date-mock-v1",
            MaximumToolPolicy = new AgentProfileToolPolicyOptions
            {
                ToolNames =
                {
                    "ask_user",
                    "aevatar_start_workflow",
                    "aevatar_observe_run",
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
                        ConnectedServiceSelectors =
                        {
                            new AgentProfileConnectedServiceSelectorOptions
                            {
                                AllowedRisks = { "read_only" },
                            },
                        },
                    },
                },
            },
        };

        var draft = NyxIdChatSystemAgentProfileDraftFactory.Create(options);

        draft.DisplayName.Should().Be("NyxID Chat Default");
        draft.Instructions.Should().Contain("workflow_id dinner_date");
        draft.RuntimeProfile.Instructions.Should().Contain("aevatar_start_workflow");
        draft.RuntimeProfile.AgentKind.Should().Be(AgentProfilePolicies.NyxIdChatAgentKind);
        draft.RuntimeProfile.RouteToolSetRef.Should().Be(AgentProfilePolicies.NyxIdChatRouteToolSet);
        draft.RuntimeProfile.ActivationMode.Should().Be(AgentProfileActivationMode.Enforced);
        draft.RuntimeProfile.PolicyRevision.Should().Be("dinner-date-mock-v1");
        draft.RuntimeProfile.MaximumToolPolicy.ToolNames.Should().Contain("aevatar_start_workflow");
        draft.RuntimeProfile.MaximumToolPolicy.ToolSetRefs.Should().Contain(AgentProfilePolicies.NyxIdChatRouteToolSet);
        draft.RuntimeProfile.MaxOwnedToolCount.Should().Be(AgentProfileValidationLimits.MaximumOwnedToolCount);
        draft.RuntimeProfile.MaxSchemaBytes.Should().Be(AgentProfileValidationLimits.MaximumSchemaBytes);
        draft.RuntimeProfile.Members.Should().ContainSingle();
        draft.RuntimeProfile.Members[0].SkillRef.Guid.Should().Be("11111111-1111-1111-1111-111111111111");
        draft.RuntimeProfile.Members[0].ExplicitTriggerAliases.Should().ContainSingle().Which.Should().Be("dinner");
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Should().ContainSingle().Which
            .CatalogServiceSlug.Should().BeEmpty();
        AgentProfilePolicies.ValidateDraft(draft).Should().BeEmpty();
    }
}
