using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class AIGAgentBaseToolRefreshTests
{
    [Fact]
    public async Task ConfigureAsync_WhenSourceToolsShrink_ShouldRemoveStaleTools()
    {
        var source = new MutableToolSource("tool-a", "tool-b");
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolSource>(source);
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent { Services = provider };

        await agent.ActivateAsync();
        agent.GetRegisteredToolNames().Should().Equal("tool-a", "tool-b");

        source.SetTools("tool-b");
        await agent.ConfigureAsync(new AIAgentConfig());

        agent.GetRegisteredToolNames().Should().Equal("tool-b");
    }

    [Fact]
    public async Task ConfigureAsync_WhenSourceToolsChanged_ShouldKeepManualTools()
    {
        var source = new MutableToolSource("source-old");
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolSource>(source);
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent { Services = provider };

        await agent.ActivateAsync();
        agent.RegisterManualTool("manual-tool");
        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-old");

        source.SetTools("source-new");
        await agent.ConfigureAsync(new AIAgentConfig());

        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-new");
    }

    private sealed class TestAIGAgent : AIGAgentBase<RoleGAgentState>
    {
        public IReadOnlyList<string> GetRegisteredToolNames() => Tools.GetAll()
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        public void RegisterManualTool(string name) => RegisterTool(new NamedTool(name));
    }

    private sealed class MutableToolSource : IAgentToolSource
    {
        private IReadOnlyList<IAgentTool> _tools;

        public MutableToolSource(params string[] toolNames)
        {
            _tools = ToTools(toolNames);
        }

        public void SetTools(params string[] toolNames)
        {
            _tools = ToTools(toolNames);
        }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_tools);
        }

        private static IReadOnlyList<IAgentTool> ToTools(IEnumerable<string> toolNames) =>
            toolNames.Select(name => (IAgentTool)new NamedTool(name)).ToList();
    }

    private sealed class NamedTool : IAgentTool
    {
        public NamedTool(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Description => Name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }
}
