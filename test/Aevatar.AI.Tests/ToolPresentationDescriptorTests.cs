using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Web.Tools;
using Aevatar.Foundation.Abstractions.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolPresentationDescriptorTests
{
    [Fact]
    public void Presentation_WhenProviderDoesNotOverride_ShouldUseGenericInvocationIdentity()
    {
        IAgentTool tool = new GenericTool();

        var presentation = tool.Presentation;

        presentation.InvocationName.Should().Be("protocol_lookup");
        presentation.DisplayName.Should().Be("protocol_lookup");
        presentation.Description.Should().Be("Looks up a value.");
        presentation.Kind.Should().Be(ToolPresentationKind.Generic);
        presentation.Availability.Should().Be(ToolAvailability.Available);
        presentation.SourceRefCase.Should().Be(ToolPresentationDescriptor.SourceRefOneofCase.None);
    }

    [Fact]
    public void BuiltInTools_ShouldExposeExplicitBuiltInDescriptors()
    {
        var options = new WebToolOptions();
        using var client = new WebApiClient(options, new HttpClient());
        IAgentTool[] tools =
        [
            new WebFetchTool(client),
            new WebSearchTool(client, options),
            new AskUserTool(),
        ];

        tools.Select(tool => tool.Presentation.Kind).Should()
            .OnlyContain(kind => kind == ToolPresentationKind.BuiltIn);
        tools.Select(tool => tool.Presentation.BuiltIn.ToolId).Should()
            .Equal("web_fetch", "web_search", "ask_user");
        tools.Select(tool => tool.Presentation.DisplayName).Should()
            .Equal("Web fetch", "Web search", "Ask user");
    }

    [Fact]
    public void McpTool_ShouldRetainProtocolAndProviderIdentitySeparately()
    {
        IAgentTool tool = new MCPToolAdapter(
            "Weather Tool!*",
            "Gets the weather.",
            "{}",
            client: null!,
            serverName: "weather-server");

        tool.Name.Should().Be("Weather_Tool");
        tool.Presentation.InvocationName.Should().Be("Weather_Tool");
        tool.Presentation.DisplayName.Should().Be("Weather Tool!*");
        tool.Presentation.Kind.Should().Be(ToolPresentationKind.Mcp);
        tool.Presentation.Mcp.ServerName.Should().Be("weather-server");
        tool.Presentation.Mcp.ToolName.Should().Be("Weather Tool!*");
    }

    [Fact]
    public void UseSkillTool_ShouldExposeSkillSourceReference()
    {
        IAgentTool tool = new UseSkillTool(new LocalSkillCatalog());

        tool.Presentation.InvocationName.Should().Be("use_skill");
        tool.Presentation.DisplayName.Should().Be("Use skill");
        tool.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        tool.Presentation.Skill.SkillName.Should().BeEmpty();
        tool.Presentation.Skill.Source.Should().Be("local-or-remote");
    }

    [Fact]
    public void UseSkillTool_ShouldResolveInvocationPresentationFromStructuredSkillArgument()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "deploy-helper",
            Description = "Deploys the selected service safely.",
            Instructions = "Follow the deployment checklist.",
            Source = SkillSource.Local,
        });
        IAgentTool tool = new UseSkillTool(catalog);

        var presentation = tool.ResolvePresentation("""{"skill":"deploy-helper"}""");

        presentation.InvocationName.Should().Be("use_skill");
        presentation.DisplayName.Should().Be("deploy-helper");
        presentation.Description.Should().Be("Deploys the selected service safely.");
        presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        presentation.Skill.SkillName.Should().Be("deploy-helper");
        presentation.Skill.Source.Should().Be("local");
    }

    [Fact]
    public async Task NyxIdBuiltInSource_ShouldExposeExplicitBuiltInDescriptors()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var httpClient = new HttpClient();
        var source = new NyxIdAgentToolSource(
            options,
            new NyxIdApiClient(options, httpClient));

        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotBeEmpty();
        tools.Should().OnlyContain(tool =>
            tool.Presentation.Kind == ToolPresentationKind.BuiltIn &&
            tool.Presentation.SourceRefCase == ToolPresentationDescriptor.SourceRefOneofCase.BuiltIn &&
            tool.Presentation.BuiltIn.ToolId == tool.Name);
    }

    private sealed class GenericTool : IAgentTool
    {
        public string Name => "protocol_lookup";
        public string Description => "Looks up a value.";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
