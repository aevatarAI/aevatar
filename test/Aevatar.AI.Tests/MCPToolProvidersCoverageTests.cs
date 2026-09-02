using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class MCPToolProvidersCoverageTests
{
    [Fact]
    public void SanitizeToolName_ShouldNormalizeAndFallback()
    {
        new MCPToolAdapter("Weather Tool!*", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("Weather_Tool");
        new MCPToolAdapter("a!!!b", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("a_b");
        new MCPToolAdapter("___", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("unnamed_tool");
        new MCPToolAdapter("name__", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("name");
    }

    [Fact]
    public async Task ExecuteAsync_WhenArgumentsJsonInvalid_ShouldReturnErrorPayload()
    {
        var adapter = new MCPToolAdapter("tool", "desc", "{}", client: null!, serverName: "srv");

        var result = await adapter.ExecuteAsync("{invalid");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientUnavailable_ShouldReturnErrorPayload()
    {
        var adapter = new MCPToolAdapter("tool", "desc", "{}", client: null!, serverName: "srv");

        var result = await adapter.ExecuteAsync("{}");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DiscoverToolsAsync_WhenServerConnectFails_ShouldReturnCachedEmptyResult()
    {
        var options = new MCPToolsOptions().AddServer("bad", "/path/does/not/exist");
        var source = new MCPAgentToolSource(options, new MCPClientManager());

        var first = await source.DiscoverToolsAsync();
        var second = await source.DiscoverToolsAsync();

        first.Should().BeEmpty();
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ConcurrentFirstUse_ShouldConnectAndDiscoverOnce()
    {
        using var discovery = new BlockingDiscoveryPort(new FakeAgentTool("mcp_echo", "{}"));
        var options = new MCPToolsOptions().AddServer("srv", "cmd");
        var source = new MCPAgentToolSource(options, discovery);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                return await source.DiscoverToolsAsync();
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await discovery.WaitForFirstDiscoveryAsync();
        discovery.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        discovery.ConnectAndDiscoverCalls.Should().Be(1);
        results.Should().OnlyContain(result => result.Count == 1 && result[0].Name == "mcp_echo");
    }

    private sealed class BlockingDiscoveryPort(params IAgentTool[] tools) : IMCPToolDiscoveryPort, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectAndDiscoverCalls;

        public int ConnectAndDiscoverCalls => Volatile.Read(ref _connectAndDiscoverCalls);

        public async Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
            MCPServerConfig config,
            CancellationToken ct = default)
        {
            _ = config;
            Interlocked.Increment(ref _connectAndDiscoverCalls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return tools;
        }

        public Task WaitForFirstDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.SetResult(true);

        public void Dispose()
        {
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
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(resultJson);
        }
    }
}
