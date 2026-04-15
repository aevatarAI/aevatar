using System.Text.Json.Nodes;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class WorkflowStudioCoverageExpansionTests
{
    private readonly WorkflowValidator _validator = new();
    private readonly WorkflowDocumentNormalizer _normalizer = new();
    private readonly WorkflowCompatibilityProfile _profile = WorkflowCompatibilityProfile.AevatarV1;

    [Fact]
    public void Validate_ShouldReportWhileErrors_WhenConditionAndMaxIterationsAreInvalid()
    {
        var findings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "loop",
                    Type = "while",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["max_iterations"] = JsonValue.Create("0"),
                    },
                },
            ],
        });

        findings.Should().Contain(f => f.Path == "/steps/0/parameters" && f.Message.Contains("requires either `condition` or a positive `max_iterations`"));
        findings.Should().Contain(f => f.Path == "/steps/0/parameters/max_iterations" && f.Message.Contains("positive integer"));
    }

    [Fact]
    public void Validate_ShouldAcceptWhile_WhenConditionIsPresent()
    {
        var findings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "loop",
                    Type = "while",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["condition"] = JsonValue.Create("{{state.ready}}"),
                    },
                },
            ],
        });

        findings.Should().NotContain(f => f.Path.Contains("/parameters") && f.Level == ValidationLevel.Error);
    }

    [Fact]
    public void Validate_ShouldReportWorkflowCallErrorsAndWarning()
    {
        var findings = _validator.Validate(
            new WorkflowDocument
            {
                Name = "wf",
                Steps =
                [
                    new StepModel
                    {
                        Id = "call",
                        Type = "workflow_call",
                        Parameters = new Dictionary<string, JsonNode?>
                        {
                            ["workflow"] = JsonValue.Create("missing-child"),
                            ["lifecycle"] = JsonValue.Create("unknown"),
                        },
                    },
                ],
            },
            new WorkflowValidationOptions
            {
                AvailableWorkflowNames = new HashSet<string>(StringComparer.Ordinal) { "known-child" },
            });

        findings.Should().Contain(f => f.Path == "/steps/0/parameters/lifecycle" && f.Level == ValidationLevel.Error);
        findings.Should().Contain(f => f.Path == "/steps/0/parameters/workflow" && f.Code == "missing_bundle_workflow");
    }

    [Fact]
    public void Validate_ShouldReportWorkflowCallMissingWorkflow()
    {
        var findings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "call",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["workflow"] = JsonValue.Create(" "),
                    },
                },
            ],
        });

        findings.Should().Contain(f => f.Path == "/steps/0/parameters/workflow" && f.Message.Contains("requires `workflow`"));
    }

    [Fact]
    public void Validate_ShouldReportFallbackErrors()
    {
        var missingFallbackFindings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "s1",
                    Type = "transform",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "fallback",
                        FallbackStep = "",
                    },
                },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        });

        missingFallbackFindings.Should().Contain(f => f.Path == "/steps/0/on_error/fallback_step" && f.Level == ValidationLevel.Error);

        var missingTargetFindings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "s1",
                    Type = "transform",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "fallback",
                        FallbackStep = "missing-step",
                    },
                },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        });

        missingTargetFindings.Should().Contain(f => f.Path == "/steps/0/on_error/fallback_step" && f.Message.Contains("does not exist"));
    }

    [Fact]
    public void Validate_ShouldReportStepTypeParameterErrors()
    {
        var findings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "s1",
                    Type = "foreach",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["sub_step_type"] = JsonValue.Create(" "),
                        ["map_step_type"] = JsonValue.Create("mystery"),
                    },
                },
            ],
        });

        findings.Should().Contain(f => f.Path == "/steps/0/parameters/sub_step_type" && f.Code == "empty_step_type_parameter");
        findings.Should().Contain(f => f.Path == "/steps/0/parameters/map_step_type" && f.Code == "unknown_parameter_step_type");
    }

    [Fact]
    public void Validate_ShouldHonorAvailableStepTypes_ForRawAndCanonicalValues()
    {
        var findings = _validator.Validate(
            new WorkflowDocument
            {
                Name = "wf",
                Steps =
                [
                    new StepModel
                    {
                        Id = "s1",
                        Type = "custom-step",
                        Parameters = new Dictionary<string, JsonNode?>
                        {
                            ["sub_step_type"] = JsonValue.Create("custom-alias"),
                        },
                    },
                ],
            },
            new WorkflowValidationOptions
            {
                AvailableStepTypes = new HashSet<string>(StringComparer.Ordinal)
                {
                    "custom-step",
                    "custom-alias",
                },
            });

        findings.Should().NotContain(f => f.Code == "unknown_step_type" || f.Code == "unknown_parameter_step_type");
    }

    [Fact]
    public void Validate_ShouldTraverseChildren_WhenNestedStepIsInvalid()
    {
        var findings = _validator.Validate(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "parent",
                    Type = "transform",
                    Children =
                    [
                        new StepModel
                        {
                            Id = "child",
                            Type = "workflow_call",
                            Parameters = new Dictionary<string, JsonNode?>
                            {
                                ["lifecycle"] = JsonValue.Create("invalid"),
                            },
                        },
                    ],
                },
            ],
        });

        findings.Should().Contain(f => f.Path == "/steps/0/children/0/parameters/workflow");
        findings.Should().Contain(f => f.Path == "/steps/0/children/0/parameters/lifecycle");
    }

    [Fact]
    public void NormalizeForExport_ShouldNormalizeRolesAndConnectorCollections()
    {
        var result = _normalizer.NormalizeForExport(new WorkflowDocument
        {
            Name = " wf ",
            Description = " desc ",
            Roles =
            [
                new RoleModel
                {
                    Id = " role-1 ",
                    Name = " ",
                    Provider = " openai ",
                    Model = " gpt-4o ",
                    EventModules = " module-a ",
                    EventRoutes = " route-a ",
                    Connectors = ["b,a", "A", " \n ", "c;B"],
                },
            ],
        });

        result.Name.Should().Be("wf");
        result.Description.Should().Be("desc");
        result.Roles[0].Id.Should().Be("role-1");
        result.Roles[0].Name.Should().Be("role-1");
        result.Roles[0].Provider.Should().Be("openai");
        result.Roles[0].Model.Should().Be("gpt-4o");
        result.Roles[0].EventModules.Should().Be("module-a");
        result.Roles[0].EventRoutes.Should().Be("route-a");
        result.Roles[0].Connectors.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyConnectorCallAndMapReduceDefaults()
    {
        var result = _normalizer.NormalizeForExport(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel { Id = "put", Type = "http_put" },
                new StepModel { Id = "delete", Type = "http_delete" },
                new StepModel
                {
                    Id = "mcp",
                    Type = "mcp_call",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["tool"] = JsonValue.Create("search"),
                    },
                },
                new StepModel { Id = "reduce", Type = "map_reduce_llm" },
            ],
        });

        result.Steps[0].Type.Should().Be("connector_call");
        result.Steps[0].Parameters["method"]!.ToString().Should().Be("PUT");
        result.Steps[1].Parameters["method"]!.ToString().Should().Be("DELETE");
        result.Steps[2].Parameters["operation"]!.ToString().Should().Be("search");
        result.Steps[3].Parameters["map_step_type"]!.ToString().Should().Be("llm_call");
        result.Steps[3].Parameters["reduce_step_type"]!.ToString().Should().Be("llm_call");
    }

    [Fact]
    public void NormalizeForExport_ShouldMirrorTimeoutAndPreserveExistingTimeoutParameter()
    {
        var result = _normalizer.NormalizeForExport(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = "wait",
                    Type = "wait_signal",
                    TimeoutMs = 1500,
                },
                new StepModel
                {
                    Id = "existing",
                    Type = "llm_call",
                    TimeoutMs = 2000,
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["timeout_ms"] = JsonValue.Create("777"),
                    },
                },
            ],
        });

        result.Steps[0].Parameters["timeout_ms"]!.ToString().Should().Be("1500");
        result.Steps[1].Parameters["timeout_ms"]!.ToString().Should().Be("777");
    }

    [Fact]
    public void NormalizeForExport_ShouldCloneComplexParametersAndPruneBlankBranches()
    {
        var complex = new JsonArray { 1, 2, 3 };
        var result = _normalizer.NormalizeForExport(new WorkflowDocument
        {
            Name = "wf",
            Steps =
            [
                new StepModel
                {
                    Id = " s1 ",
                    Type = "foreach_llm",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["sub_step_type"] = JsonValue.Create("loop"),
                        ["payload"] = complex,
                        [""] = JsonValue.Create("ignored"),
                    },
                    Next = " s2 ",
                    Branches = new Dictionary<string, string>
                    {
                        ["ok"] = " s2 ",
                        [" "] = "s3",
                        ["skip"] = " ",
                    },
                    Children =
                    [
                        new StepModel { Id = " child ", Type = "http_post" },
                    ],
                },
                new StepModel { Id = "s2", Type = "transform" },
            ],
        });

        result.Steps[0].Type.Should().Be("foreach");
        result.Steps[0].Parameters["sub_step_type"]!.ToString().Should().Be("while");
        result.Steps[0].Parameters["payload"].Should().BeOfType<JsonArray>().And.NotBeSameAs(complex);
        result.Steps[0].Parameters.Should().NotContainKey("");
        result.Steps[0].Next.Should().Be("s2");
        result.Steps[0].Branches.Should().ContainSingle().Which.Should().BeEquivalentTo(new KeyValuePair<string, string>("ok", "s2"));
        result.Steps[0].Children[0].Type.Should().Be("connector_call");
        result.Steps[0].Children[0].Parameters["method"]!.ToString().Should().Be("POST");
    }

    [Fact]
    public void WorkflowCompatibilityProfile_ShouldCoverClosedWorldAndLifecycleBranches()
    {
        _profile.IsCanonicalStepType("transform").Should().BeTrue();
        _profile.IsCanonicalStepType("actor_send").Should().BeFalse();
        _profile.IsClosedWorldBlocked("transform").Should().BeFalse();
        _profile.IsSupportedWorkflowCallLifecycle(null).Should().BeTrue();
        _profile.IsSupportedWorkflowCallLifecycle("scope").Should().BeTrue();
        _profile.IsSupportedWorkflowCallLifecycle("invalid").Should().BeFalse();
        _profile.ShouldMirrorTimeoutMsToParameters("wait_signal").Should().BeTrue();
        _profile.ShouldMirrorTimeoutMsToParameters("transform").Should().BeFalse();
    }

    [Fact]
    public void WorkflowCompatibilityProfile_ShouldNormalizeBlankTokensAndUnknownTypes()
    {
        _profile.ToCanonicalType(" ").Should().BeEmpty();
        _profile.ToCanonicalType(" custom ").Should().Be("custom");
        _profile.IsStepTypeParameterKey(null).Should().BeFalse();
        _profile.IsStepTypeParameterKey("step").Should().BeTrue();
        _profile.IsKnownStepType("unknown-type").Should().BeFalse();
        _profile.IsKnownStepType("workflow_loop").Should().BeTrue();
    }
}
