using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Voice;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Voice;

public class AgentToolVoiceCatalogTests
{
    [Fact]
    public async Task DiscoverAsync_ShouldProjectStructuredDefinitions()
    {
        var catalog = new AgentToolVoiceCatalog([
            new StubToolSource(new FakeAgentTool(
                "door.open",
                "opens the front door",
                """{"type":"object","properties":{"door":{"type":"string"}}}""")),
        ]);

        var definitions = await catalog.DiscoverAsync();

        definitions.Should().ContainSingle();
        definitions[0].Name.Should().Be("door.open");
        definitions[0].Description.Should().Be("opens the front door");
        definitions[0].ParametersSchema.Should().Contain("\"door\"");
    }

    [Fact]
    public async Task DiscoverAsync_ShouldCacheDiscoveredTools()
    {
        var source = new CountingToolSource(new FakeAgentTool("door.open", "fake", "{}"));
        var catalog = new AgentToolVoiceCatalog([source]);

        await catalog.DiscoverAsync();
        await catalog.DiscoverAsync();

        source.DiscoverCalls.Should().Be(1);
    }

    private sealed class StubToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class CountingToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public int DiscoverCalls { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            DiscoverCalls++;
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class FakeAgentTool(string name, string description, string parametersSchema) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public string ParametersSchema { get; } = parametersSchema;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _ = ct;
            return Task.FromResult("""{"ok":true}""");
        }
    }
}
