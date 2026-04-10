using System.Text.Json.Nodes;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowValidatorTests
{
    private readonly WorkflowValidator _validator = new();

    private static WorkflowDocument MinimalValid(string name = "wf", string stepType = "transform") =>
        new()
        {
            Name = name,
            Roles = [new RoleModel { Id = "role1" }],
            Steps = [new StepModel { Id = "s1", Type = stepType, TargetRole = "role1" }],
        };

    [Fact]
    public void Validate_ShouldReportError_WhenNameIsEmpty()
    {
        var doc = MinimalValid() with { Name = "" };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path == "/name" && f.Level == ValidationLevel.Error);
    }

    [Fact]
    public void Validate_ShouldReportError_WhenNoSteps()
    {
        var doc = new WorkflowDocument { Name = "wf", Steps = [] };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path == "/steps" && f.Level == ValidationLevel.Error);
    }

    [Fact]
    public void Validate_ShouldPass_WhenMinimalValid()
    {
        var findings = _validator.Validate(MinimalValid());
        findings.Where(f => f.Level == ValidationLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReportError_WhenDuplicateRoleIds()
    {
        var doc = MinimalValid() with
        {
            Roles = [new RoleModel { Id = "r" }, new RoleModel { Id = "r" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("Duplicate role id"));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenEmptyRoleId()
    {
        var doc = MinimalValid() with
        {
            Roles = [new RoleModel { Id = "" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path == "/roles/0/id" && f.Level == ValidationLevel.Error);
    }

    [Fact]
    public void Validate_ShouldReportError_WhenDuplicateStepIds()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = "r" }],
            Steps =
            [
                new StepModel { Id = "dup", Type = "transform" },
                new StepModel { Id = "dup", Type = "transform" },
            ],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("Duplicate step id"));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenEmptyStepId()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "", Type = "transform" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path == "/steps/0/id" && f.Level == ValidationLevel.Error);
    }

    [Fact]
    public void Validate_ShouldReportError_WhenUnknownStepType()
    {
        var findings = _validator.Validate(MinimalValid(stepType: "nonexistent"));
        findings.Should().Contain(f => f.Code == "unknown_step_type");
    }

    [Fact]
    public void Validate_ShouldReportError_WhenForbiddenStepType()
    {
        var findings = _validator.Validate(MinimalValid(stepType: "workflow_loop"));
        findings.Should().Contain(f => f.Code == "forbidden_step_type");
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenImportOnlyStepType()
    {
        var findings = _validator.Validate(MinimalValid(stepType: "actor_send"));
        findings.Should().Contain(f => f.Code == "import_only_step_type" && f.Level == ValidationLevel.Warning);
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenAliasUsed()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "while", OriginalType = "loop", TargetRole = "role1" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "alias_normalized");
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenRoleAliasUsed()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "transform", TargetRole = "role1", UsedRoleAlias = true }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "role_alias");
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenChildrenPresent()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform", TargetRole = "role1",
                Children = [new StepModel { Id = "child", Type = "transform" }],
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "children_import_only");
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenComplexParameters()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform", TargetRole = "role1",
                Parameters = new Dictionary<string, JsonNode?> { ["data"] = new JsonObject() },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "complex_parameter");
    }

    [Fact]
    public void Validate_ShouldReportError_WhenTargetRoleMissing()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "transform", TargetRole = "nonexistent" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "missing_role");
    }

    [Fact]
    public void Validate_ShouldReportError_WhenNextStepMissing()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "transform", Next = "missing" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "missing_next");
    }

    [Fact]
    public void Validate_ShouldReportError_WhenBranchTargetMissing()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "conditional",
                Branches = new Dictionary<string, string>
                {
                    ["true"] = "missing",
                    ["false"] = "s1",
                },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path.Contains("branches/true"));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenBranchTargetIsEmpty()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "conditional",
                Branches = new Dictionary<string, string> { ["true"] = "", ["false"] = "s1" },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Path.Contains("branches/true") && f.Message.Contains("missing a target"));
    }

    [Fact]
    public void Validate_ShouldReportWarning_WhenImplicitSequentialOrdering()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = "r" }],
            Steps =
            [
                new StepModel { Id = "s1", Type = "transform" },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "implicit_next");
    }

    [Fact]
    public void Validate_Conditional_ShouldRequireTrueAndFalseBranches()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "conditional" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`true` branch"));
        findings.Should().Contain(f => f.Message.Contains("`false` branch"));
    }

    [Fact]
    public void Validate_Switch_ShouldRequireDefaultBranch()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "switch",
                Branches = new Dictionary<string, string> { ["case1"] = "s1" },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`_default` branch"));
    }

    [Fact]
    public void Validate_While_ShouldRequireConditionOrMaxIterations()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "while" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`condition` or a positive `max_iterations`"));
    }

    [Fact]
    public void Validate_While_ShouldReportError_WhenMaxIterationsNotPositive()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "while",
                Parameters = new Dictionary<string, JsonNode?> { ["max_iterations"] = JsonValue.Create("0") },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`max_iterations` must be a positive integer"));
    }

    [Fact]
    public void Validate_While_ShouldPass_WhenConditionSet()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "while",
                Parameters = new Dictionary<string, JsonNode?> { ["condition"] = JsonValue.Create("x > 0") },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Where(f => f.Level == ValidationLevel.Error && f.Path.Contains("parameters")).Should().BeEmpty();
    }

    [Fact]
    public void Validate_WorkflowCall_ShouldRequireWorkflowParameter()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel { Id = "s1", Type = "workflow_call" }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`workflow_call` requires `workflow`"));
    }

    [Fact]
    public void Validate_WorkflowCall_ShouldReportError_WhenInvalidLifecycle()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "workflow_call",
                Parameters = new Dictionary<string, JsonNode?>
                {
                    ["workflow"] = JsonValue.Create("child"),
                    ["lifecycle"] = JsonValue.Create("invalid"),
                },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("lifecycle"));
    }

    [Fact]
    public void Validate_WorkflowCall_ShouldWarn_WhenBundleWorkflowMissing()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "workflow_call",
                Parameters = new Dictionary<string, JsonNode?>
                {
                    ["workflow"] = JsonValue.Create("missing-wf"),
                },
            }],
        };
        var options = new WorkflowValidationOptions
        {
            AvailableWorkflowNames = new HashSet<string>(["other"]),
        };
        var findings = _validator.Validate(doc, options);
        findings.Should().Contain(f => f.Code == "missing_bundle_workflow");
    }

    [Fact]
    public void Validate_OnErrorFallback_ShouldRequireFallbackStep()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform",
                OnError = new StepErrorPolicy { Strategy = "fallback" },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("`fallback` strategy requires `fallback_step`"));
    }

    [Fact]
    public void Validate_OnErrorFallback_ShouldReportError_WhenFallbackStepMissing()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform",
                OnError = new StepErrorPolicy { Strategy = "fallback", FallbackStep = "missing" },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Message.Contains("does not exist"));
    }

    [Fact]
    public void Validate_ShouldReportError_WhenStepTypeParameterEmpty()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "foreach",
                Parameters = new Dictionary<string, JsonNode?> { ["sub_step_type"] = JsonValue.Create("") },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "empty_step_type_parameter");
    }

    [Fact]
    public void Validate_ShouldReportError_WhenStepTypeParameterUnknown()
    {
        var doc = MinimalValid() with
        {
            Steps = [new StepModel
            {
                Id = "s1", Type = "foreach",
                Parameters = new Dictionary<string, JsonNode?> { ["sub_step_type"] = JsonValue.Create("nonexistent") },
            }],
        };
        var findings = _validator.Validate(doc);
        findings.Should().Contain(f => f.Code == "unknown_parameter_step_type");
    }

    [Fact]
    public void Validate_ShouldAcceptCustomStepTypes_WhenAvailableStepTypesProvided()
    {
        var doc = MinimalValid(stepType: "my_custom_step");
        var options = new WorkflowValidationOptions
        {
            AvailableStepTypes = new HashSet<string>(["my_custom_step"]),
        };
        var findings = _validator.Validate(doc, options);
        findings.Should().NotContain(f => f.Code == "unknown_step_type");
    }
}
