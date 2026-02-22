using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class WorkflowValidatorCoverageTests
{
    [Fact]
    public void Validate_ShouldReportMissingNameAndMissingSteps()
    {
        var wf = new WorkflowDefinition
        {
            Name = " ",
            Roles = [],
            Steps = [],
        };

        var errors = WorkflowValidator.Validate(wf);

        errors.Should().Contain("缺少 name");
        errors.Should().Contain("至少需要一个 step");
    }

    [Fact]
    public void Validate_ShouldReportIdRoleNextAndBranchErrors()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition { Id = "r1", Name = "Role1" },
            ],
            Steps =
            [
                new StepDefinition
                {
                    Id = "",
                    Type = "llm_call",
                    TargetRole = "r1",
                },
                new StepDefinition
                {
                    Id = "stepA",
                    Type = "conditional",
                    TargetRole = "missing-role",
                    Next = "missing-next",
                    Branches = new Dictionary<string, string>
                    {
                        ["empty"] = "",
                        ["missing"] = "missing-branch-step",
                    },
                },
            ],
        };

        var errors = WorkflowValidator.Validate(wf);

        errors.Should().Contain(e => e.Contains("缺少 id"));
        errors.Should().Contain(e => e.Contains("missing-role"));
        errors.Should().Contain(e => e.Contains("missing-next"));
        errors.Should().Contain(e => e.Contains("分支 'empty'"));
        errors.Should().Contain(e => e.Contains("missing-branch-step"));
    }

    [Fact]
    public void Validate_ShouldEnumerateNestedChildrenAndDetectDuplicateIds()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition { Id = "r1", Name = "Role1" },
            ],
            Steps =
            [
                new StepDefinition
                {
                    Id = "root",
                    Type = "parallel",
                    TargetRole = "r1",
                    Children =
                    [
                        new StepDefinition
                        {
                            Id = "child-1",
                            Type = "llm_call",
                            TargetRole = "r1",
                        },
                        new StepDefinition
                        {
                            Id = "child-1",
                            Type = "llm_call",
                            TargetRole = "r1",
                        },
                    ],
                },
            ],
        };

        var errors = WorkflowValidator.Validate(wf);

        errors.Should().Contain(e => e.Contains("重复"));
    }
}
