using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.Serialization;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowEditorServiceStructureTests
{
    [Theory]
    [InlineData("true: finish\nfalse: {}", "/steps/0/branches/false")]
    [InlineData("- condition: true\n  next: finish\n- condition: false", "/steps/0/branches/1")]
    public void ParseYaml_WhenBranchTargetIsMissing_ShouldRejectBeforeCreatingDocument(
        string branches,
        string expectedPath)
    {
        var service = CreateEditor();

        var result = service.ParseYaml(new ParseYamlRequest(BuildWorkflowWithBranches(branches)));

        result.Document.Should().BeNull();
        result.Graph.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Level == ValidationLevel.Error &&
            finding.Code == "yaml_structure" &&
            finding.Path == expectedPath);
    }

    [Fact]
    public void ParseYaml_WhenChildIsNull_ShouldReturnStructureFindingInsteadOfPartialDocument()
    {
        var service = CreateEditor();

        var result = service.ParseYaml(new ParseYamlRequest("""
            name: null_child
            steps:
              - id: parent
                type: assign
                children:
                  - null
            """));

        result.Document.Should().BeNull();
        result.Graph.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Level == ValidationLevel.Error &&
            finding.Code == "yaml_structure" &&
            finding.Path == "/steps/0/children/0");
        result.Findings.Should().NotContain(finding => finding.Code == "runtime_validation");
    }

    [Fact]
    public void ParseYaml_WhenYamlSyntaxIsInvalid_ShouldKeepSyntaxDiagnosticDistinct()
    {
        var service = CreateEditor();

        var result = service.ParseYaml(new ParseYamlRequest("name: invalid\nsteps: ["));

        result.Document.Should().BeNull();
        result.Graph.Should().BeNull();
        result.Findings.Should().ContainSingle(finding => finding.Code == "yaml_syntax");
    }

    [Theory]
    [InlineData("true: { next: finish }\nfalse: finish")]
    [InlineData("- condition: true\n  next: finish\n- condition: false\n  next: finish")]
    public void ParseAndSerializeYaml_WhenBranchesAndComplexParametersAreValid_ShouldPreserveThem(string branches)
    {
        var service = CreateEditor();

        var parsed = service.ParseYaml(new ParseYamlRequest(BuildWorkflowWithBranches(branches)));

        parsed.Document.Should().NotBeNull();
        parsed.Findings.Should().NotContain(finding => finding.Level == ValidationLevel.Error);
        parsed.Document!.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["true"] = "finish",
            ["false"] = "finish",
        });
        var originalValue = parsed.Document.Steps[1].Parameters["value"].Should()
            .BeOfType<StudioStepObjectParameterValue>().Which;

        var serialized = service.SerializeYaml(new SerializeYamlRequest(parsed.Document));
        var roundTrip = service.ParseYaml(new ParseYamlRequest(serialized.Yaml));

        serialized.Findings.Should().NotContain(finding => finding.Level == ValidationLevel.Error);
        roundTrip.Findings.Should().NotContain(finding => finding.Level == ValidationLevel.Error);
        roundTrip.Document!.Steps[0].Branches.Should().BeEquivalentTo(parsed.Document.Steps[0].Branches);
        var roundTripValue = roundTrip.Document.Steps[1].Parameters["value"].Should()
            .BeOfType<StudioStepObjectParameterValue>().Which;
        roundTripValue.Properties.Keys.Should().BeEquivalentTo("items", "options");
        var items = roundTripValue.Properties["items"].Should().BeOfType<StudioStepListParameterValue>().Which;
        items.Items.Should().HaveCount(3);
        items.Items[0].Should().BeOfType<StudioStepScalarParameterValue>().Which.Scalar.Should().Be("one");
        var nested = items.Items[2].Should().BeOfType<StudioStepObjectParameterValue>().Which;
        nested.Properties.Should().ContainSingle();
        nested.Properties["nested"].Should().BeOfType<StudioStepScalarParameterValue>().Which.Scalar.Should().Be("two");
        var options = roundTripValue.Properties["options"].Should().BeOfType<StudioStepObjectParameterValue>().Which;
        options.Properties.Should().ContainSingle();
        options.Properties["enabled"].Should().BeOfType<StudioStepScalarParameterValue>().Which.Scalar.Should().Be("true");
        roundTripValue.ToWorkflowScalarString().Should().Be(originalValue.ToWorkflowScalarString());
    }

    [Fact]
    public void ParseYaml_WhenDocumentAdapterMissesInvalidStructure_ShouldKeepRuntimeStructurePathAndCode()
    {
        var service = CreateEditor(new PermissiveDocumentService());

        var result = service.ParseYaml(new ParseYamlRequest(
            BuildWorkflowWithBranches("true: finish\nfalse: {}")));

        result.Document.Should().BeNull();
        result.Graph.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Path == "/steps/0/branches/false" &&
            finding.Code == "yaml_structure" &&
            finding.Level == ValidationLevel.Error);
        result.Findings.Should().NotContain(finding => finding.Code == "runtime_validation");
    }

    private static WorkflowEditorService CreateEditor(IWorkflowYamlDocumentService? documentService = null)
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;
        return new WorkflowEditorService(
            documentService ?? new YamlWorkflowDocumentService(profile),
            new WorkflowDocumentNormalizer(profile),
            new WorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            new TextDiffService());
    }

    private static string BuildWorkflowWithBranches(string branches)
    {
        var indentedBranches = string.Join('\n', branches.Split('\n').Select(line => $"      {line}"));
        return $$"""
            name: branch_structure
            roles: []
            steps:
              - id: choose
                type: conditional
                branches:
            {{indentedBranches}}
              - id: finish
                type: assign
                parameters:
                  target: result
                  value:
                    items: [one, null, { nested: two }]
                    options:
                      enabled: true
            """;
    }

    private sealed class PermissiveDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) => new(
            new WorkflowDocument
            {
                Name = "adapter_document",
                Steps = [new StepModel { Id = "finish", Type = "assign" }],
            },
            []);

        public string Serialize(WorkflowDocument document) => throw new NotSupportedException();
    }
}
