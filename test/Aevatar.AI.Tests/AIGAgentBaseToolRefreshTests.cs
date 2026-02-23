using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
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
        services.AddSingleton<IEventStore, TestEventStore>();
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
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
        services.AddSingleton<IEventStore, TestEventStore>();
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
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

    private sealed class TestEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<long> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            stream.AddRange(events.Select(x => x.Clone()));
            return Task.FromResult(stream.Count == 0 ? 0 : stream[^1].Version);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
