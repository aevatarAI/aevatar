using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Voice;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Voice;

public class AgentToolVoiceInvokerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldResolveToolFromSources()
    {
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(new FakeAgentTool("door.open", """{"ok":true}""")),
        ]);

        var result = await invoker.ExecuteAsync("door.open", """{"target":"front"}""");

        result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowWhenToolMissing()
    {
        var invoker = new AgentToolVoiceInvoker([]);

        var act = () => invoker.ExecuteAsync("missing", "{}");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tool 'missing' not found");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCacheDiscoveredTools()
    {
        var source = new CountingToolSource(new FakeAgentTool("door.open", """{"ok":true}"""));
        var invoker = new AgentToolVoiceInvoker([source]);

        await invoker.ExecuteAsync("door.open", "{}");
        await invoker.ExecuteAsync("door.open", "{}");

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

    private sealed class FakeAgentTool(string name, string resultJson) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _ = ct;
            return Task.FromResult(resultJson);
        }
    }
}
