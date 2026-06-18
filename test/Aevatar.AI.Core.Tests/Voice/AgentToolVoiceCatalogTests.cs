using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Voice;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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

    [Fact]
    public async Task DiscoverAsync_ConcurrentFirstUse_ShouldStartSourceDiscoveryOnce()
    {
        using var source = new BlockingCountingToolSource(new FakeAgentTool("door.open", "fake", "{}"));
        var catalog = new AgentToolVoiceCatalog([source]);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                return await catalog.DiscoverAsync();
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await source.WaitForFirstDiscoveryAsync();
        source.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        source.DiscoverCalls.Should().Be(1);
        results.Should().OnlyContain(result => result.Count == 1 && result[0].Name == "door.open");
    }

    [Fact]
    public async Task DiscoverAsync_WithCredentialRef_ShouldResolvePerCallContextWithoutUsingGlobalCache()
    {
        var source = new ContextSensitiveToolSource();
        var credentials = new StubCredentialProvider(("voice-tool:ref-1", "caller-token-123"));
        var catalog = new AgentToolVoiceCatalog([source], credentials);
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        var callerDefinitions = await catalog.DiscoverAsync(toolContext);
        var anonymousDefinitions = await catalog.DiscoverAsync();

        callerDefinitions.Should().ContainSingle(definition => definition.Name == "caller.only");
        anonymousDefinitions.Should().ContainSingle(definition => definition.Name == "anonymous.only");
        source.CapturedNyxIdAccessTokens.Should().BeEquivalentTo(["caller-token-123", null]);
        credentials.RequestedRefs.Should().ContainSingle().Which.Should().Be("voice-tool:ref-1");
        source.DiscoverCalls.Should().Be(2);
    }

    [Fact]
    public async Task DiscoverAsync_WithDifferentCredentialRefs_ShouldNotReuseCallerScopedSnapshot()
    {
        var source = new TokenNamedToolSource();
        var credentials = new StubCredentialProvider(
            ("voice-tool:ref-1", "caller-token-123"),
            ("voice-tool:ref-2", "caller-token-456"));
        var catalog = new AgentToolVoiceCatalog([source], credentials);

        var firstDefinitions = await catalog.DiscoverAsync(CreateToolContext("voice-tool:ref-1"));
        var secondDefinitions = await catalog.DiscoverAsync(CreateToolContext("voice-tool:ref-2"));

        firstDefinitions.Should().ContainSingle(definition => definition.Name == "caller-token-123.only");
        secondDefinitions.Should().ContainSingle(definition => definition.Name == "caller-token-456.only");
        credentials.RequestedRefs.Should().Equal("voice-tool:ref-1", "voice-tool:ref-2");
        source.CapturedNyxIdAccessTokens.Should().Equal("caller-token-123", "caller-token-456");
        source.DiscoverCalls.Should().Be(2);
    }

    [Fact]
    public async Task DiscoverAsync_WithCredentialRef_ShouldMapVoiceBusinessContextAndRestrictVisibleTools()
    {
        var allowedTool = new FakeAgentTool("door.open", "allowed", "{}");
        var deniedTool = new FakeAgentTool("lights.toggle", "denied", "{}");
        var source = new CapturingToolSource([allowedTool, deniedTool]);
        var credentials = new StubCredentialProvider(("voice-tool:ref-2", "caller-token-456"));
        var catalog = new AgentToolVoiceCatalog([source], credentials);
        var toolContext = CreateFullToolContext("voice-tool:ref-2");

        var definitions = await catalog.DiscoverAsync(toolContext);

        definitions.Should().ContainSingle().Which.Name.Should().Be("door.open");
        var captured = source.CapturedContexts.Should().ContainSingle().Which;
        captured.Credentials.NyxIdAccessToken.Should().Be("caller-token-456");
        captured.Caller.ScopeId.Should().Be("caller-scope-1");
        captured.Caller.OwnerSubject.Should().Be("owner-subject-1");
        captured.Caller.ResponseId.Should().Be("response-1");
        captured.Channel.Platform.Should().Be("lark");
        captured.Channel.SenderId.Should().Be("sender-1");
        captured.Channel.RegistrationScopeId.Should().Be("registration-scope-1");
        captured.Channel.MessageId.Should().Be("message-1");
        captured.Channel.PlatformMessageId.Should().Be("platform-message-1");
        captured.Channel.DeliveryTargetId.Should().Be("delivery-1");
        captured.SenderBinding.BindingId.Should().Be("sender-binding-1");
        captured.Routing.NyxIdRoutePreference.Should().Be("direct");
        captured.ConnectedServices.ContextJson.Should().Be("""{"service":"ctx"}""");
        captured.ToolVisibility.IsRestricted.Should().BeTrue();
        captured.ToolVisibility.Allows("door.open").Should().BeTrue();
        captured.ToolVisibility.Allows("lights.toggle").Should().BeFalse();
    }

    private static VoiceToolExecutionContext CreateFullToolContext(string credentialRef)
    {
        var toolContext = CreateToolContext(credentialRef);
        toolContext.CallerScopeId = " caller-scope-1 ";
        toolContext.OwnerSubject = " owner-subject-1 ";
        toolContext.ResponseId = " response-1 ";
        toolContext.ChannelPlatform = " lark ";
        toolContext.ChannelSenderId = " sender-1 ";
        toolContext.ChannelRegistrationScopeId = " registration-scope-1 ";
        toolContext.ChannelMessageId = " message-1 ";
        toolContext.ChannelPlatformMessageId = " platform-message-1 ";
        toolContext.ChannelDeliveryTargetId = " delivery-1 ";
        toolContext.SenderBindingId = " sender-binding-1 ";
        toolContext.NyxIdRoutePreference = " direct ";
        toolContext.ConnectedServicesContextJson = """ {"service":"ctx"} """;
        toolContext.AllowedToolNames.Add(" door.open ");
        return toolContext;
    }

    private static VoiceToolExecutionContext CreateToolContext(string credentialRef) =>
        new()
        {
            CredentialRef = credentialRef,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };

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

    private sealed class CapturingToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public List<AgentToolExecutionContext> CapturedContexts { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            CapturedContexts.Add(AgentToolRequestContext.Current!);
            return Task.FromResult(tools);
        }
    }

    private sealed class ContextSensitiveToolSource : IAgentToolSource
    {
        public int DiscoverCalls { get; private set; }
        public List<string?> CapturedNyxIdAccessTokens { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            DiscoverCalls++;
            var token = AgentToolRequestContext.NyxIdAccessToken;
            CapturedNyxIdAccessTokens.Add(token);
            IAgentTool tool = string.IsNullOrWhiteSpace(token)
                ? new FakeAgentTool("anonymous.only", "anonymous", "{}")
                : new FakeAgentTool("caller.only", "caller", "{}");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class TokenNamedToolSource : IAgentToolSource
    {
        public int DiscoverCalls { get; private set; }
        public List<string?> CapturedNyxIdAccessTokens { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            DiscoverCalls++;
            var token = AgentToolRequestContext.NyxIdAccessToken;
            CapturedNyxIdAccessTokens.Add(token);
            return Task.FromResult<IReadOnlyList<IAgentTool>>(
            [
                new FakeAgentTool($"{token}.only", "caller", "{}"),
            ]);
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
