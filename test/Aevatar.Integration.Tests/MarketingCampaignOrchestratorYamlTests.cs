using Aevatar.Configuration;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class MarketingCampaignOrchestratorYamlTests
{
    [Fact]
    public void MarketingCampaignOrchestratorYaml_ShouldParseAndValidate()
    {
        var yaml = File.ReadAllText(GetWorkflowPath());
        var workflow = new WorkflowParser().Parse(yaml);
        var errors = WorkflowValidator.Validate(workflow);

        errors.Should().BeEmpty();
        workflow.Name.Should().Be("marketing_campaign_orchestrator");
        workflow.Roles.Select(role => role.Id).Should().ContainInOrder(
            "request_parser",
            "source_resolver",
            "discovery_analyst",
            "marketing_strategist",
            "delivery_operator",
            "content_exporter",
            "web_researcher");
        workflow.Roles.Single(role => role.Id == "web_researcher")
            .Connectors.Should().Equal("marketing_search_mcp", "marketing_extract_mcp", "marketing_social_extract_mcp");
        workflow.Roles.Single(role => role.Id == "content_exporter")
            .Connectors.Should().Equal("marketing_markdown_export");

        workflow.Steps.Select(step => step.Id).Should().Contain(
            "build_pricing_page_query",
            "search_pricing_page",
            "infer_pricing_page_url",
            "route_pricing_page_source",
            "build_customer_page_query",
            "search_customer_page",
            "infer_customer_page_url",
            "route_customer_page_source",
            "build_feature_page_query",
            "search_feature_page",
            "infer_feature_page_url",
            "route_feature_page_source",
            "normalize_research_bundle",
            "build_competitor_search_query",
            "search_competitors",
            "search_competitor_social_signals",
            "extract_competitor_social_evidence",
            "save_competitor_social_evidence",
            "search_competitor_content_examples",
            "search_engagement_benchmarks",
            "competitor_landscape_report",
            "viral_pattern_report",
            "save_research_bundle_normalized",
            "save_delivery_bundle",
            "build_export_slug",
            "export_delivery_bundle",
            "save_delivery_export_result",
            "compose_delivery_result");

        workflow.Steps.Select(step => step.Id).Should().ContainInOrder(
            "capture_raw_input",
            "parse_request",
            "save_request_brief",
            "resolve_brand_url",
            "save_brand_url",
            "route_brand_source",
            "build_brand_lookup_query",
            "save_brand_lookup_query",
            "discover_brand_url",
            "infer_brand_url_from_search",
            "save_discovered_brand_url",
            "extract_homepage",
            "build_research_bundle",
            "normalize_research_bundle",
            "save_research_bundle_normalized",
            "build_competitor_search_query",
            "save_competitor_search_query",
            "search_competitors",
            "save_competitor_search_results",
            "build_competitor_social_query",
            "save_competitor_social_query",
            "search_competitor_social_signals",
            "save_competitor_social_signals",
            "search_competitor_social_profiles",
            "save_competitor_social_profiles",
            "search_competitor_recent_activity",
            "save_competitor_recent_activity",
            "extract_competitor_social_evidence",
            "save_competitor_social_evidence",
            "build_competitor_content_examples_query",
            "save_competitor_content_examples_query",
            "search_competitor_content_examples",
            "save_competitor_content_examples",
            "build_engagement_benchmark_query",
            "save_engagement_benchmark_query",
            "search_engagement_benchmarks",
            "save_engagement_benchmark_results",
            "competitor_landscape_report",
            "save_competitor_landscape_report",
            "viral_pattern_report",
            "save_viral_pattern_report",
            "discovery_report",
            "save_discovery_report",
            "strategy_report",
            "save_strategy_report",
            "delivery_bundle",
            "save_delivery_bundle",
            "export_delivery_bundle",
            "compose_delivery_result");
    }

    private static string GetWorkflowPath() =>
        Path.Combine(AevatarPaths.RepoRootWorkflows, "marketing_campaign_orchestrator.yaml");
}
