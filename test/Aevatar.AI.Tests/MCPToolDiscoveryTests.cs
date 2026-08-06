using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class MCPToolDiscoveryTests
{
    [Fact]
    public void AddRemoteServer_ShouldConfigureStreamableHttpEndpointAndHeaders()
    {
        var options = new MCPToolsOptions().AddRemoteServer(
            "remote",
            "https://mcp.example.com/mcp",
            new Dictionary<string, string> { ["x-tenant"] = "demo" });

        options.Servers.Should().ContainSingle();
        options.Servers[0].Command.Should().BeEmpty();
        options.Servers[0].Url.Should().Be("https://mcp.example.com/mcp");
        options.Servers[0].AdditionalHeaders["x-tenant"].Should().Be("demo");
    }

    [Fact]
    public async Task DiscoverToolsAsync_WhenServerConnectFails_ShouldReturnEmptyResultAndAllowRetry()
    {
        var options = new MCPToolsOptions().AddServer("bad", "/path/does/not/exist");
        var source = new MCPAgentToolSource(options, new MCPClientManager());

        var first = await source.DiscoverToolsAsync();
        var second = await source.DiscoverToolsAsync();

        first.Should().BeEmpty();
        second.Should().BeEmpty();
        ReferenceEquals(first, second).Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ConcurrentFirstUse_ShouldConnectAndDiscoverOnce()
    {
        using var discovery = new BlockingDiscoveryPort(new FakeAgentTool("mcp_echo"));
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

    [Fact]
    public async Task DiscoverToolsAsync_WhenServerTtlIsZero_ShouldRefreshBeforeNextRead()
    {
        var discovery = new CountingDiscoveryPort(TimeSpan.Zero, new FakeAgentTool("mcp_echo"));
        var options = new MCPToolsOptions().AddServer("srv", "cmd");
        var source = new MCPAgentToolSource(options, discovery);

        var first = await source.DiscoverToolsAsync();
        var second = await source.DiscoverToolsAsync();

        discovery.ConnectAndDiscoverCalls.Should().Be(2);
        first.Should().ContainSingle(x => x.Name == "mcp_echo");
        second.Should().ContainSingle(x => x.Name == "mcp_echo");
    }

    private sealed class BlockingDiscoveryPort(params IAgentTool[] tools) : IMCPToolDiscoveryPort, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectAndDiscoverCalls;

        public int ConnectAndDiscoverCalls => Volatile.Read(ref _connectAndDiscoverCalls);

        public async Task<MCPToolDiscoveryResult> ConnectAndDiscoverAsync(
            MCPServerConfig config,
            CancellationToken ct = default)
        {
            _ = config;
            Interlocked.Increment(ref _connectAndDiscoverCalls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return new MCPToolDiscoveryResult(tools, null);
        }

        public Task WaitForFirstDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.SetResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class CountingDiscoveryPort(
        TimeSpan? timeToLive,
        params IAgentTool[] tools) : IMCPToolDiscoveryPort
    {
        private int _connectAndDiscoverCalls;

        public int ConnectAndDiscoverCalls => Volatile.Read(ref _connectAndDiscoverCalls);

        public Task<MCPToolDiscoveryResult> ConnectAndDiscoverAsync(
            MCPServerConfig config,
            CancellationToken ct = default)
        {
            _ = config;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _connectAndDiscoverCalls);
            return Task.FromResult(new MCPToolDiscoveryResult(tools, timeToLive));
        }
    }

    private sealed class FakeAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }
}
