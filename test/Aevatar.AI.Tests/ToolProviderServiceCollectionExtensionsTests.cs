using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class ToolProviderServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddMCPTools_WhenCalledTwice_ShouldRemainIdempotentForToolSourceRegistration()
    {
        var services = new ServiceCollection();

        services.AddMCPTools(_ => { });
        services.AddMCPTools(_ => { });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Count(x => x is MCPAgentToolSource).Should().Be(1);
    }

    [Fact]
    public async Task AddSkills_WhenCalledTwice_ShouldRemainIdempotentForToolSourceRegistration()
    {
        var services = new ServiceCollection();

        services.AddSkills(_ => { });
        services.AddSkills(_ => { });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Count(x => x is SkillsAgentToolSource).Should().Be(1);
    }

    [Fact]
    public async Task AddMCPToolsAndAddSkills_ShouldRegisterBothToolSources()
    {
        var services = new ServiceCollection();

        services.AddMCPTools(_ => { });
        services.AddSkills(_ => { });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Should().ContainSingle(x => x is MCPAgentToolSource);
        sources.Should().ContainSingle(x => x is SkillsAgentToolSource);
    }
}
