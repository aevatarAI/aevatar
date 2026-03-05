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

    [Fact]
    public void Validate_WhenWhileMissingTermination_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "loop-1",
                    Type = "while",
                },
            ],
        };

        var errors = WorkflowValidator.Validate(wf);
        errors.Should().Contain(e => e.Contains("while") && e.Contains("condition"));
    }

    [Fact]
    public void Validate_WhenClosedWorldModeContainsBlockedPrimitive_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "s1",
                    Type = "llm_call",
                },
            ],
        };

        var errors = WorkflowValidator.Validate(wf);
        errors.Should().Contain(e => e.Contains("closed_world_mode"));
    }

    [Fact]
    public void Validate_WhenClosedWorldModeContainsDynamicWorkflow_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "s1",
                    Type = "dynamic_workflow",
                },
            ],
        };

        var errors = WorkflowValidator.Validate(wf);
        errors.Should().Contain(e => e.Contains("closed_world_mode") && e.Contains("dynamic_workflow"));
    }

    [Fact]
    public void Validate_WhenDynamicWorkflowDisallowed_ShouldReportReservedPrimitiveError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "ensure_runtime_ready",
                    Type = "dynamic_workflow",
                },
            ],
        };

        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                DisallowDynamicWorkflowStep = true,
            },
            availableWorkflowNames: null);

        errors.Should().Contain(error => error.Contains("保留原语") && error.Contains("dynamic_workflow"));
    }

    [Fact]
    public void Validate_WhenWorkflowCallTargetIsUnknown_ShouldReportErrorWhenRegistryProvided()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call-sub",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["workflow"] = "sub_flow",
                    },
                },
            ],
        };

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "another_flow" };
        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                RequireResolvableWorkflowCallTargets = true,
            },
            availableWorkflowNames: available);

        errors.Should().Contain(e => e.Contains("sub_flow"));
    }

    [Fact]
    public void Validate_WhenWorkflowCallLifecycleUnknown_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call-sub",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["workflow"] = "sub_flow",
                        ["lifecycle"] = "isolate",
                    },
                },
            ],
        };

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sub_flow" };
        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                RequireResolvableWorkflowCallTargets = true,
            },
            availableWorkflowNames: available);

        errors.Should().Contain(e =>
            e.Contains("workflow_call") &&
            e.Contains("lifecycle") &&
            e.Contains("singleton/transient/scope"));
    }

    [Theory]
    [InlineData("singleton")]
    [InlineData("transient")]
    [InlineData("scope")]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_WhenWorkflowCallLifecycleMissingOrKnown_ShouldNotReportLifecycleError(string lifecycle)
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call-sub",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["workflow"] = "sub_flow",
                        ["lifecycle"] = lifecycle,
                    },
                },
            ],
        };

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sub_flow" };
        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                RequireResolvableWorkflowCallTargets = true,
            },
            availableWorkflowNames: available);

        errors.Should().NotContain(e => e.Contains("lifecycle"));
    }

    [Fact]
    public void Validate_WhenStepTypeIsUnknownAndKnownTypesRequired_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "m1",
                    Type = "mystery_step",
                },
            ],
        };

        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "assign",
                    "transform",
                    "while",
                },
            },
            availableWorkflowNames: null);

        errors.Should().Contain(e => e.Contains("未知原语") && e.Contains("mystery_step"));
    }

    [Fact]
    public void Validate_WhenStepTypeParameterIsUnknownAndKnownTypesRequired_ShouldReportError()
    {
        var wf = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "loop-1",
                    Type = "while",
                    Parameters = new Dictionary<string, string>
                    {
                        ["step"] = "mystery_sub_step",
                        ["max_iterations"] = "1",
                    },
                },
            ],
        };

        var errors = WorkflowValidator.Validate(
            wf,
            options: new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "while",
                    "assign",
                },
            },
            availableWorkflowNames: null);

        errors.Should().Contain(e =>
            e.Contains("参数 'step'") &&
            e.Contains("未知原语") &&
            e.Contains("mystery_sub_step"));
    }
}
