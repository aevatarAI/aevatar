using System.Reflection;
using System.Text.Json.Nodes;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class WorkflowStudioBranchCoverageTests
{
    [Fact]
    public void CompatibilityProfile_ShouldExerciseAdditionalHelperBranches()
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;

        profile.ToCanonicalType("  HTTP_GET ").Should().Be("connector_call");
        profile.ToCanonicalType((string?)null).Should().BeEmpty();
        profile.IsKnownStepType("connector_call").Should().Be(true);
        profile.IsKnownStepType("made_up_step").Should().Be(false);
        profile.IsCanonicalStepType("http_get").Should().Be(true);
        profile.IsCanonicalStepType("http get").Should().Be(false);
        profile.IsStepTypeParameterKey("step").Should().Be(true);
        profile.IsStepTypeParameterKey("map_step_type").Should().Be(true);
        profile.IsStepTypeParameterKey((string?)null).Should().Be(false);
        profile.IsSupportedWorkflowCallLifecycle("scope").Should().Be(true);
        profile.IsSupportedWorkflowCallLifecycle("invalid").Should().Be(false);
        profile.ShouldMirrorTimeoutMsToParameters("http_get")
            .Should()
            .NotBe(profile.ShouldMirrorTimeoutMsToParameters("transform"));
    }

    [Fact]
    public void NormalizeForExport_ShouldHandleNestedParametersAndStepVariants()
    {
        var normalizer = CreateWithOptionalProfile<WorkflowDocumentNormalizer>();
        var document = new WorkflowDocument
        {
            Name = "coverage",
            Roles =
            [
                new RoleModel
                {
                    Id = "planner",
                    Name = " Planner ",
                    SystemPrompt = "test",
                    Connectors = [" beta ", "", "alpha", "beta", "alpha "]
                }
            ],
            Steps =
            [
                new StepModel
                {
                    Id = "http",
                    Type = "HTTP_GET",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = "https://example.com",
                        ["headers"] = new JsonObject
                        {
                            ["keep"] = "x",
                            ["drop"] = "",
                            ["nested"] = new JsonObject
                            {
                                ["innerKeep"] = "y",
                                ["innerDrop"] = ""
                            }
                        },
                        ["items"] = new JsonArray("", JsonValue.Create(1), new JsonObject { ["v"] = "ok", ["blank"] = "" })
                    }
                },
                new StepModel
                {
                    Id = "call",
                    Type = "workflow_call",
                    TimeoutMs = 2500,
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["workflow"] = "child-flow",
                        ["lifecycle"] = "scope"
                    }
                },
                new StepModel
                {
                    Id = "reduce",
                    Type = "map_reduce",
                    Children =
                    [
                        new StepModel
                        {
                            Id = "child",
                            Type = "LLM",
                            Parameters = new Dictionary<string, JsonNode?>
                            {
                                ["prompt"] = "hello"
                            }
                        }
                    ]
                },
                new StepModel
                {
                    Id = "connector",
                    Type = "connector_call",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["connector"] = " chrono "
                    }
                }
            ]
        };

        var normalized = normalizer.NormalizeForExport(document);

        normalized.Should().NotBeSameAs(document);
        normalized.Roles.Should().ContainSingle();
        normalized.Roles[0].Connectors.Should().Equal("alpha", "beta");
        normalized.Steps.Should().HaveCount(4);
        normalized.Steps[0].Type.Should().Be("connector_call");
        normalized.Steps[0].Parameters.Should().ContainKey("headers");
        normalized.Steps[1].TimeoutMs.Should().Be(2500);
        normalized.Steps[2].Children.Should().ContainSingle();
        normalized.Steps[2].Children[0].Type.Should().Be("llm_call");
        normalized.Steps[3].Type.Should().Be("connector_call");
    }

    [Fact]
    public void Validate_ShouldReportMultipleInvalidTypeSpecificBranches()
    {
        var validator = CreateWithOptionalProfile<WorkflowValidator>();
        var document = new WorkflowDocument
        {
            Configuration = new WorkflowConfiguration
            {
                ClosedWorldMode = true
            },
            Steps =
            [
                new StepModel
                {
                    Id = "cond",
                    Type = "conditional",
                    Branches = new Dictionary<string, string>()
                },
                new StepModel
                {
                    Id = "loop",
                    Type = "while",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["max_iterations"] = JsonValue.Create(-1)
                    }
                },
                new StepModel
                {
                    Id = "call",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["lifecycle"] = "invalid"
                    }
                },
                new StepModel
                {
                    Id = "fallback",
                    Type = "llm",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "fallback"
                    }
                },
                new StepModel
                {
                    Id = "parent",
                    Type = "map_reduce",
                    Children =
                    [
                        new StepModel
                        {
                            Id = "child",
                            Type = "made_up_step"
                        }
                    ]
                }
            ]
        };

        var findings = validator.Validate(document);

        findings.Should().NotBeEmpty();
        findings.Should().Contain(f => f.Level == ValidationLevel.Error);
        findings.Should().Contain(f => f.Path.Contains("/steps/0", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Path.Contains("/steps/1", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Path.Contains("/steps/2", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Path.Contains("/steps/3", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Path.Contains("/steps/4/children/0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldAcceptNestedWorkflowCallWhenConfigurationIsSane()
    {
        var validator = CreateWithOptionalProfile<WorkflowValidator>();
        var document = new WorkflowDocument
        {
            Name = "ok",
            Steps =
            [
                new StepModel
                {
                    Id = "call",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, JsonNode?>
                    {
                        ["workflow"] = "child-flow",
                        ["lifecycle"] = "scope",
                        ["timeout_ms"] = JsonValue.Create(1200)
                    },
                    Children =
                    [
                        new StepModel
                        {
                            Id = "child",
                            Type = "llm_call",
                            Parameters = new Dictionary<string, JsonNode?>
                            {
                                ["prompt"] = "ok"
                            }
                        }
                    ]
                }
            ]
        };

        var findings = validator.Validate(document);

        findings.Should().NotContain(f => f.Level == ValidationLevel.Error);
    }

    private static T CreateWithOptionalProfile<T>()
        where T : class
    {
        var type = typeof(T);
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
        if (parameterless is not null)
        {
            return (T)parameterless.Invoke([]);
        }

        var singleProfile = constructors.FirstOrDefault(c =>
        {
            var parameters = c.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(WorkflowCompatibilityProfile);
        });

        if (singleProfile is not null)
        {
            return (T)singleProfile.Invoke([WorkflowCompatibilityProfile.AevatarV1]);
        }

        throw new InvalidOperationException($"Unable to construct {type.FullName}.");
    }

}
