using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Voice;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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

    [Fact]
    public async Task ExecuteAsync_ConcurrentFirstUse_ShouldStartSourceDiscoveryOnce()
    {
        using var source = new BlockingCountingToolSource(new FakeAgentTool("door.open", """{"ok":true}"""));
        var invoker = new AgentToolVoiceInvoker([source]);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                return await invoker.ExecuteAsync("door.open", "{}");
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await source.WaitForFirstDiscoveryAsync();
        source.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        source.DiscoverCalls.Should().Be(1);
        results.Should().OnlyContain(result => result == """{"ok":true}""");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveCredentialRefAndExposeCallerNyxIdTokenToTool()
    {
        var captured = new CapturingAgentTool("nyxid_proxy");
        var credentials = new StubCredentialProvider(("voice-tool:ref-1", "caller-token-123"));
        var invoker = new AgentToolVoiceInvoker([new StubToolSource(captured)], credentials);
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        await invoker.ExecuteAsync("nyxid_proxy", "{}", toolContext);

        credentials.RequestedRefs.Should().ContainSingle().Which.Should().Be("voice-tool:ref-1");
        captured.CapturedNyxIdAccessToken.Should().Be("caller-token-123");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSetCredentialContext_WhenNoScope()
    {
        var captured = new CapturingAgentTool("nyxid_proxy");
        var invoker = new AgentToolVoiceInvoker([new StubToolSource(captured)]);

        await invoker.ExecuteAsync("nyxid_proxy", "{}");

        captured.CapturedNyxIdAccessToken.Should().BeNull();
    }

    private sealed class CapturingAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "capturing";
        public string ParametersSchema => "{}";
        public string? CapturedNyxIdAccessToken { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _ = ct;
            CapturedNyxIdAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            return Task.FromResult("{}");
        }
    }

    private sealed class StubToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class StubCredentialProvider(params (string Ref, string Token)[] credentials) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _credentials = credentials.ToDictionary(
            static credential => credential.Ref,
            static credential => credential.Token,
            StringComparer.Ordinal);

        public List<string> RequestedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            _ = ct;
            RequestedRefs.Add(credentialRef);
            return Task.FromResult(_credentials.GetValueOrDefault(credentialRef));
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

    private sealed class BlockingCountingToolSource(params IAgentTool[] tools) : IAgentToolSource, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _discoverCalls;

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _discoverCalls);
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
            _ = ct;
            return Task.FromResult(resultJson);
        }
    }
}
