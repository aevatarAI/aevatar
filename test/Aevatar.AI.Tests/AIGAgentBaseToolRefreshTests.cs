using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
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
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

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
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        await agent.ActivateAsync();
        agent.RegisterManualTool("manual-tool");
        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-old");

        source.SetTools("source-new");
        await agent.ConfigureAsync(new AIAgentConfig());

        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-new");
    }

    private sealed class TestAIGAgent : AIGAgentBase<RoleGAgentState>
    {
        public TestAIGAgent(IEnumerable<IAgentToolSource> toolSources)
            : base(
                new StubLLMProviderFactory(),
                Array.Empty<IAIGAgentExecutionHook>(),
                Array.Empty<IAgentRunMiddleware>(),
                Array.Empty<IToolCallMiddleware>(),
                Array.Empty<ILLMCallMiddleware>(),
                toolSources)
        {
        }

        public IReadOnlyList<string> GetRegisteredToolNames() => Tools.GetAll()
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        public void RegisterManualTool(string name) => RegisterTool(new NamedTool(name));

        protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(RoleGAgentState state)
        {
            _ = state;
            return new AIAgentConfigStateOverrides();
        }
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

    private sealed class StubLLMProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => new StubLLMProvider(name);
        public ILLMProvider GetDefault() => new StubLLMProvider("default");
        public IReadOnlyList<string> GetAvailableProviders() => ["default"];
    }

    private sealed class StubLLMProvider(string name) : ILLMProvider
    {
        public string Name => name;

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            return Task.FromResult(new LLMResponse { Content = "ok" });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            await Task.CompletedTask;
            yield break;
        }
    }

}
