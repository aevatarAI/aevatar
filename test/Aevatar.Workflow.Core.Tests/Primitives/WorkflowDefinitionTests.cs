using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowDefinitionTests
{
    [Fact]
    public void EntryStepId_WhenNoSteps_ShouldBeNull()
    {
        var workflow = BuildWorkflow([]);

        workflow.EntryStepId.Should().BeNull();
    }

    [Fact]
    public void GetStep_ShouldReturnMatchingStep_OrNull()
    {
        var workflow = BuildWorkflow(
            new StepDefinition { Id = "s1", Type = "llm_call" },
            new StepDefinition { Id = "s2", Type = "transform" });

        workflow.GetStep("s2")!.Id.Should().Be("s2");
        workflow.GetStep("missing").Should().BeNull();
    }

    [Fact]
    public void GetNextStep_WhenCurrentStepMissing_ShouldReturnNull()
    {
        var workflow = BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" });

        workflow.GetNextStep("missing").Should().BeNull();
    }

    [Fact]
    public void GetNextStep_WithExplicitBranchMatch_ShouldReturnBranchTarget()
    {
        var workflow = BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "conditional",
                Branches = new Dictionary<string, string>
                {
                    ["yes"] = "s2",
                    ["_default"] = "s3",
                },
            },
            new StepDefinition { Id = "s2", Type = "transform" },
            new StepDefinition { Id = "s3", Type = "transform" });

        workflow.GetNextStep("s1", "yes")!.Id.Should().Be("s2");
    }

    [Fact]
    public void GetNextStep_WithDefaultBranch_ShouldUseDefaultTarget()
    {
        var workflow = BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "conditional",
                Branches = new Dictionary<string, string>
                {
                    ["_default"] = "s3",
                },
            },
            new StepDefinition { Id = "s2", Type = "transform" },
            new StepDefinition { Id = "s3", Type = "transform" });

        workflow.GetNextStep("s1", "unknown")!.Id.Should().Be("s3");
    }

    [Fact]
    public void GetNextStep_WhenBranchTargetsMissing_ShouldFallbackToNextPointer()
    {
        var workflow = BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "conditional",
                Next = "s4",
                Branches = new Dictionary<string, string>
                {
                    ["yes"] = "missing-yes",
                    ["_default"] = "missing-default",
                },
            },
            new StepDefinition { Id = "s2", Type = "transform" },
            new StepDefinition { Id = "s3", Type = "transform" },
            new StepDefinition { Id = "s4", Type = "transform" });

        workflow.GetNextStep("s1", "yes")!.Id.Should().Be("s4");
    }

    [Fact]
    public void GetNextStep_WhenNoNextPointer_ShouldUseSequentialOrder()
    {
        var workflow = BuildWorkflow(
            new StepDefinition { Id = "s1", Type = "llm_call" },
            new StepDefinition { Id = "s2", Type = "transform" });

        workflow.GetNextStep("s1")!.Id.Should().Be("s2");
        workflow.GetNextStep("s2").Should().BeNull();
    }

    [Fact]
    public void GetNextStep_WhenNextPointerMissingTarget_ShouldReturnNull()
    {
        var workflow = BuildWorkflow(
            new StepDefinition { Id = "s1", Type = "llm_call", Next = "missing" },
            new StepDefinition { Id = "s2", Type = "transform" });

        workflow.GetNextStep("s1").Should().BeNull();
    }

    private static WorkflowDefinition BuildWorkflow(IReadOnlyList<StepDefinition> steps)
    {
        return new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "coordinator",
                    Name = "Coordinator",
                },
            ],
            Steps = steps.ToList(),
        };
    }

    private static WorkflowDefinition BuildWorkflow(params StepDefinition[] steps)
        => BuildWorkflow((IReadOnlyList<StepDefinition>)steps);
}
