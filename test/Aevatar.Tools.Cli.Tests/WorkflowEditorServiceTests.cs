using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.Serialization;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class WorkflowEditorServiceTests
{
    [Fact]
    public void ParseYaml_ShouldTreatRuntimeCustomPrimitiveAsKnown_WhenRuntimeCatalogProvidesIt()
    {
        var service = CreateService();

        var result = service.ParseYaml(new ParseYamlRequest(
            """
            name: demo_template
            steps:
              - id: render_message
                type: demo_template
                parameters:
                  template: "Incident {{input}}"
            """,
            AvailableWorkflowNames: [],
            AvailableStepTypes: ["demo_template"]));

        result.Document.Should().NotBeNull();
        result.Findings.Should().NotContain(finding => finding.Code == "unknown_step_type");
    }

    [Fact]
    public void ParseYaml_ShouldReportUnknownStepType_WhenRuntimeCatalogDoesNotProvideIt()
    {
        var service = CreateService();

        var result = service.ParseYaml(new ParseYamlRequest(
            """
            name: demo_template
            steps:
              - id: render_message
                type: demo_template
            """,
            AvailableWorkflowNames: []));

        result.Findings.Should().ContainSingle(finding => finding.Code == "unknown_step_type");
    }

    [Fact]
    public void ParseYaml_ShouldSurfaceRuntimeValidationFailures_InStudioFindings()
    {
        var service = CreateService();

        var result = service.ParseYaml(new ParseYamlRequest(
            """
            name: demo_runtime_only_validation
            steps:
              - id: run_actor
                type: actor_send
                parameters:
                  agent_type: "bad agent type!"
            """,
            AvailableStepTypes: ["actor_send"]));

        result.Document.Should().NotBeNull();
        result.Findings.Should().Contain(finding =>
            finding.Code == "runtime_validation" &&
            finding.Message.Contains("agent_type", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowEditorService CreateService()
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;
        return new WorkflowEditorService(
            new YamlWorkflowDocumentService(profile),
            new WorkflowDocumentNormalizer(profile),
            new WorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            new TextDiffService());
    }
}
