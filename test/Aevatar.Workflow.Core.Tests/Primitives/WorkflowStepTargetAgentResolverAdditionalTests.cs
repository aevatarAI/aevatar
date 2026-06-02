using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowStepTargetAgentResolverAdditionalTests
{
    [Fact]
    public void ResolveEffectiveTargetRole_WhenLlmCallOmitsTargetRole_ShouldUseImplicitAssistant()
    {
        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: null,
            stepType: "llm_call");

        role.Should().Be(WorkflowImplicitLlmRolePolicy.DefaultRoleId);
    }

    [Theory]
    [InlineData("evaluate")]
    [InlineData("reflect")]
    public void ResolveEffectiveTargetRole_WhenWorkflowOwnedLlmStepOmitsTargetRole_ShouldUseWorkflowRunActor(string stepType)
    {
        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: null,
            stepType: stepType);

        role.Should().BeEmpty();
    }

    [Fact]
    public void ResolveEffectiveTargetRole_WhenConfiguredTargetRoleHasWhitespace_ShouldTrimIt()
    {
        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: " reviewer ",
            stepType: "evaluate");

        role.Should().Be("reviewer");
    }

    [Fact]
    public void ResolveEffectiveTargetRole_WhenStepDoesNotNeedImplicitRole_ShouldReturnEmpty()
    {
        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: " ",
            stepType: "transform");

        role.Should().BeEmpty();
    }

    [Fact]
    public void ResolveEffectiveTargetRole_WhenExplicitDefaultRoleExistsForReflect_ShouldUseWorkflowRunActor()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition { Id = " assistant ", Name = "Configured assistant" },
            ],
            Steps = [],
        };

        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow,
            configuredTargetRole: null,
            stepType: "reflect");

        role.Should().BeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_ShouldNotCreateImplicitRoleForNestedWorkflowOwnedLlmStepWithoutTargetRole()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition { Id = "writer", Name = "Writer" },
            ],
            Steps =
            [
                new StepDefinition
                {
                    Id = "parent",
                    Type = "loop",
                    Children =
                    [
                        new StepDefinition { Id = "judge", Type = "evaluate" },
                    ],
                },
            ],
        };

        var roles = WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(workflow);

        roles.Should().ContainSingle();
        roles[0].Id.Should().Be("writer");
    }

    [Fact]
    public void GetEffectiveRoles_ShouldNotCreateImplicitRole_WhenExplicitDefaultRoleExists()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition { Id = " assistant ", Name = "Assistant" },
            ],
            Steps =
            [
                new StepDefinition { Id = "judge", Type = "evaluate" },
            ],
        };

        var roles = WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(workflow);

        roles.Should().ContainSingle();
        roles[0].Id.Should().Be(" assistant ");
    }

    [Fact]
    public void WorkflowChatSessionKeys_ShouldValidateRequiredInputs()
    {
        FluentActions.Invoking(() => WorkflowChatSessionKeys.CreateWorkflowStepSessionId(" ", "step"))
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("scopeId");
        FluentActions.Invoking(() => WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", " "))
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("stepId");
        FluentActions.Invoking(() => WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", " ", "step"))
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("runId");
        FluentActions.Invoking(() => WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", "run", " "))
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("stepId");
        FluentActions.Invoking(() => WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", "run", "step", 0))
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("attempt");
    }

    [Fact]
    public void WorkflowChatSessionKeys_ShouldComposeWorkflowStepSessionIds()
    {
        WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", "step")
            .Should()
            .Be("scope:step");
        WorkflowChatSessionKeys.CreateWorkflowStepSessionId("scope", "run", "step", 2)
            .Should()
            .Be("scope:run:step:a2");
    }
}
