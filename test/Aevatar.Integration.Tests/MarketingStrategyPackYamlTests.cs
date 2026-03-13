using Aevatar.Configuration;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class MarketingStrategyPackYamlTests
{
    [Fact]
    public void MarketingStrategyPackYaml_ShouldParseAndValidate()
    {
        var yaml = File.ReadAllText(GetWorkflowPath());
        var workflow = new WorkflowParser().Parse(yaml);
        var errors = WorkflowValidator.Validate(workflow);

        errors.Should().BeEmpty();
        workflow.Name.Should().Be("marketing_strategy_pack");
        workflow.Roles.Select(role => role.Id).Should().ContainInOrder(
            "request_parser",
            "strategy_planner",
            "copywriter",
            "delivery_operator",
            "content_exporter");
        workflow.Roles.Single(role => role.Id == "content_exporter")
            .Connectors.Should().Equal("marketing_markdown_export");

        workflow.Steps.Select(step => step.Id).Should().ContainInOrder(
            "capture_raw_input",
            "parse_strategy_input",
            "save_strategy_inputs",
            "build_strategy_foundation",
            "save_strategy_foundation",
            "build_content_calendar",
            "save_content_calendar",
            "build_asset_briefs",
            "save_asset_briefs",
            "build_delivery_bundle",
            "save_delivery_bundle",
            "build_export_slug",
            "save_export_slug",
            "export_delivery_bundle",
            "save_delivery_export_result",
            "compose_delivery_result");
    }

    private static string GetWorkflowPath() =>
        Path.Combine(AevatarPaths.RepoRootWorkflows, "marketing_strategy_pack.yaml");
}
