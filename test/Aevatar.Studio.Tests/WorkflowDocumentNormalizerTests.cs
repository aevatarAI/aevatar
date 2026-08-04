using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDocumentNormalizerTests
{
    private readonly WorkflowDocumentNormalizer _normalizer = new();

    [Fact]
    public void NormalizeForExport_ShouldTrimNameAndDescription()
    {
        var doc = new WorkflowDocument { Name = "  wf  ", Description = "  desc  " };
        var result = _normalizer.NormalizeForExport(doc);
        result.Name.Should().Be("wf");
        result.Description.Should().Be("desc");
    }

    [Fact]
    public void NormalizeForExport_ShouldHandleNullDescription()
    {
        var doc = new WorkflowDocument { Name = "wf", Description = null! };
        var result = _normalizer.NormalizeForExport(doc);
        result.Description.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeForExport_ShouldNormalizeRoleIdAndName()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = " r1 ", Name = " Role One " }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Roles[0].Id.Should().Be("r1");
        result.Roles[0].Name.Should().Be("Role One");
    }

    [Fact]
    public void NormalizeForExport_ShouldUseIdAsNameWhenNameIsBlank()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = "myRole", Name = "  " }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Roles[0].Name.Should().Be("myRole");
    }

    [Fact]
    public void NormalizeForExport_ShouldDeduplicateAndSortConnectors()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = "r1", Connectors = ["b,a", "A"] }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Roles[0].Connectors.Should().BeEquivalentTo(["a", "b"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void NormalizeForExport_ShouldSplitConnectorsBySemicolonAndNewline()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Roles = [new RoleModel { Id = "r1", Connectors = ["x;y\nz"] }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Roles[0].Connectors.Should().BeEquivalentTo(["x", "y", "z"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void NormalizeForExport_ShouldCanonicalizeStepType()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "loop" }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Type.Should().Be("while");
    }

    [Fact]
    public void NormalizeForExport_ShouldPreserveNestedNyxIdRequestBodyRequirement()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf-alpha",
            Steps =
            [
                new StepModel
                {
                    Id = "parallel-alpha",
                    Type = "parallel",
                    Children =
                    [
                        new StepModel
                        {
                            Id = "request-alpha",
                            Type = "tool_call",
                            Capability = new StepCapability
                            {
                                NyxIdRequest = new NyxIdRequestCapability
                                {
                                    UserServiceId = " usvc-alpha ",
                                    Method = " POST ",
                                    PathTemplate = " /api/resources ",
                                    BodyRequired = true,
                                    BodyMode = " json ",
                                    ResponseMode = " text ",
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var result = _normalizer.NormalizeForExport(doc);

        var request = result.Steps.Should().ContainSingle().Which.Children
            .Should().ContainSingle().Which.Capability!.NyxIdRequest!;
        request.BodyRequired.Should().BeTrue();
        request.UserServiceId.Should().Be("usvc-alpha");
        request.Method.Should().Be("POST");
    }

    [Fact]
    public void NormalizeForExport_ShouldResetUsedRoleAlias()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "transform", UsedRoleAlias = true }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].UsedRoleAlias.Should().BeFalse();
    }

    [Fact]
    public void NormalizeForExport_ShouldTrimStepIdAndTargetRole()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = " s1 ", Type = "transform", TargetRole = " r1 " }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Id.Should().Be("s1");
        result.Steps[0].TargetRole.Should().Be("r1");
    }

    [Fact]
    public void NormalizeForExport_ShouldSkipBlankParameterKeys()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform",
                Parameters = new StudioStepParameters { [""] = StudioStepParameterValue.FromScalar("v"), ["key"] = StudioStepParameterValue.FromScalar("v") },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("key");
        result.Steps[0].Parameters.Should().NotContainKey("");
    }

    [Fact]
    public void NormalizeForExport_ShouldCanonicalizeStepTypeParameters()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "foreach",
                Parameters = new StudioStepParameters { ["sub_step_type"] = StudioStepParameterValue.FromScalar("loop") },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters["sub_step_type"]!.ToWorkflowScalarString().Should().Be("while");
    }

    [Fact]
    public void NormalizeForExport_ShouldPreserveComplexParameters()
    {
        var complex = StudioStepParameterValue.FromObject([new KeyValuePair<string, StudioStepParameterValue?>("nested", StudioStepParameterValue.FromScalar("value"))]);
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform",
                Parameters = new StudioStepParameters { ["data"] = complex },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters["data"].Should().BeAssignableTo<StudioStepParameterValue>()
            .Which.IsComplexValue().Should().BeTrue();
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyHttpGetDefaults()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "http_get" }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("method");
        result.Steps[0].Parameters["method"]!.ToWorkflowScalarString().Should().Be("GET");
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyHttpPostDefaults()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "http_post" }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters["method"]!.ToWorkflowScalarString().Should().Be("POST");
    }

    [Fact]
    public void NormalizeForExport_ShouldNotOverrideExistingHttpMethod()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "http_get",
                Parameters = new StudioStepParameters { ["method"] = StudioStepParameterValue.FromScalar("PATCH") },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters["method"]!.ToWorkflowScalarString().Should().Be("PATCH");
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyForeachLlmDefaults()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "foreach_llm" }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("sub_step_type");
        result.Steps[0].Parameters["sub_step_type"]!.ToWorkflowScalarString().Should().Be("llm_call");
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyMapReduceLlmDefaults()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "map_reduce_llm" }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("map_step_type");
        result.Steps[0].Parameters.Should().ContainKey("reduce_step_type");
    }

    [Fact]
    public void NormalizeForExport_ShouldApplyMcpCallToolToOperation()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "mcp_call",
                Parameters = new StudioStepParameters { ["tool"] = StudioStepParameterValue.FromScalar("my-tool") },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("operation");
        result.Steps[0].Parameters["operation"]!.ToWorkflowScalarString().Should().Be("my-tool");
    }

    [Fact]
    public void NormalizeForExport_ShouldMirrorTimeoutMsToParameters()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "llm_call", TimeoutMs = 5000 }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().ContainKey("timeout_ms");
        result.Steps[0].Parameters["timeout_ms"]!.ToWorkflowScalarString().Should().Be("5000");
    }

    [Fact]
    public void NormalizeForExport_ShouldNotMirrorTimeoutMs_WhenNotApplicable()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel { Id = "s1", Type = "transform", TimeoutMs = 5000 }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters.Should().NotContainKey("timeout_ms");
    }

    [Fact]
    public void NormalizeForExport_ShouldFilterBlankBranches()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "switch",
                Branches = new Dictionary<string, string> { ["ok"] = "s1", [""] = "s1", ["empty"] = "" },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Branches.Should().ContainKey("ok");
        result.Steps[0].Branches.Should().NotContainKey("");
        result.Steps[0].Branches.Should().NotContainKey("empty");
    }

    [Fact]
    public void NormalizeForExport_ShouldNormalizeChildStepsRecursively()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "foreach",
                Children = [new StepModel { Id = " child ", Type = "loop" }],
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Children[0].Id.Should().Be("child");
        result.Steps[0].Children[0].Type.Should().Be("while");
    }

    [Fact]
    public void NormalizeForExport_ShouldHandleNullParameterValue()
    {
        var doc = new WorkflowDocument
        {
            Name = "wf",
            Steps = [new StepModel
            {
                Id = "s1", Type = "transform",
                Parameters = new StudioStepParameters { ["key"] = null },
            }],
        };
        var result = _normalizer.NormalizeForExport(doc);
        result.Steps[0].Parameters["key"].Should().BeNull();
    }
}
