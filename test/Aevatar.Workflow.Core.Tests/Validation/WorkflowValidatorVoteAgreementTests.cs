using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Validation;

public sealed class WorkflowValidatorVoteAgreementTests
{
    [Theory]
    [InlineData("unknown", "unknown vote agreement rule mode")]
    [InlineData("quorum", "quorum mode requires")]
    public void Validate_WhenVoteRuleInvalid_ShouldReturnError(string mode, string expected)
    {
        var workflow = Workflow(
            Step(
                "vote",
                "vote",
                new Dictionary<string, string>
                {
                    ["rule_mode"] = mode,
                }));

        var errors = WorkflowValidator.Validate(workflow);

        errors.Should().Contain(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenVoteQuorumInvalid_ShouldReturnError()
    {
        var workflow = Workflow(
            Step(
                "vote",
                "vote",
                new Dictionary<string, string>
                {
                    ["rule_mode"] = "quorum",
                    ["quorum_ratio"] = "1.5",
                }));

        WorkflowValidator.Validate(workflow).Should().Contain(e => e.Contains("quorum_ratio"));
    }

    [Fact]
    public void Validate_WhenVoteCountConstraintInvalid_ShouldReturnError()
    {
        var workflow = Workflow(
            Step(
                "vote",
                "vote",
                new Dictionary<string, string>
                {
                    ["rule_mode"] = "label_count_constraints",
                    ["min_approve_count"] = "-1",
                }));

        WorkflowValidator.Validate(workflow).Should().Contain(e => e.Contains("min_count"));
    }

    [Fact]
    public void Validate_WhenLabelSourceAnnotationMissingField_ShouldReturnError()
    {
        var workflow = Workflow(
            Step(
                "vote",
                "vote",
                new Dictionary<string, string>
                {
                    ["rule_mode"] = "majority",
                    ["label_source"] = "annotation",
                }));

        WorkflowValidator.Validate(workflow).Should().Contain(e => e.Contains("label_field"));
    }

    [Fact]
    public void Validate_WhenConfiguredDecisionBranchMissing_ShouldReturnError()
    {
        var workflow = Workflow(
            new StepDefinition
            {
                Id = "vote",
                Type = "vote",
                Parameters =
                {
                    ["rule_mode"] = "majority",
                    ["on_agreed"] = "accepted",
                },
                Branches = new Dictionary<string, string>
                {
                    ["rejected"] = "end",
                },
            },
            Step("end", "assign"));

        WorkflowValidator.Validate(workflow).Should().Contain(e => e.Contains("on_agreed"));
    }

    [Fact]
    public void Validate_WhenParallelNestedVoteRuleInvalid_ShouldReturnError()
    {
        var workflow = Workflow(
            Step(
                "fanout",
                "parallel",
                new Dictionary<string, string>
                {
                    ["vote_step_type"] = "vote",
                    ["vote_param_rule_mode"] = "quorum",
                    ["vote_param_quorum_count"] = "0",
                }));

        WorkflowValidator.Validate(workflow).Should().Contain(e => e.Contains("parallel.vote"));
    }

    [Fact]
    public void Validate_WhenVoteStepTypeUnknown_ShouldStillFailKnownStepValidation()
    {
        var workflow = Workflow(
            Step(
                "fanout",
                "parallel",
                new Dictionary<string, string>
                {
                    ["vote_step_type"] = "structured_agreement",
                }));

        var errors = WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.ToHashSet(StringComparer.OrdinalIgnoreCase),
            },
            availableWorkflowNames: null);

        errors.Should().Contain(e => e.Contains("structured_agreement"));
    }

    private static WorkflowDefinition Workflow(params StepDefinition[] steps) =>
        new()
        {
            Name = "wf",
            Roles = [],
            Steps = steps.ToList(),
        };

    private static StepDefinition Step(
        string id,
        string type,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new()
        {
            Id = id,
            Type = type,
            Parameters = parameters?.ToDictionary(x => x.Key, x => x.Value) ?? [],
        };
}
