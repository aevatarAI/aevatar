using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.LLMProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class FailoverLLMProviderFactoryTests
{
    [Fact]
    public async Task GetProvider_WhenPrimaryMissing_ShouldResolveFromFallback()
    {
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "fallback-ok" }),
        };
        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(throwOnGetProvider: new InvalidOperationException("primary missing")),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        var provider = factory.GetProvider("openai");
        var response = await provider.ChatAsync(new LLMRequest { Messages = [] });

        response.Content.Should().Be("fallback-ok");
    }

    [Fact]
    public async Task ChatAsync_WhenPrimaryThrows_ShouldFallback()
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;

        var primaryProvider = new StubProvider("openai")
        {
            OnChatAsync = (_, _) =>
            {
                primaryCalls++;
                throw new InvalidOperationException("primary failed");
            },
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "fallback-response" }),
            OnChatAsyncWithCounter = () => fallbackCalls++,
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        var response = await factory.GetProvider("openai").ChatAsync(new LLMRequest { Messages = [] });

        primaryCalls.Should().Be(1);
        fallbackCalls.Should().Be(1);
        response.Content.Should().Be("fallback-response");
    }

    [Fact]
    public async Task GetProvider_WhenPreferFallbackDefaultEnabled_ShouldUseFallbackDefaultProvider()
    {
        var fallbackDefaultCalls = 0;
        var fallbackNamedCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatAsync = (_, _) => throw new InvalidOperationException("primary failed"),
        };
        var fallbackDefaultProvider = new StubProvider("deepseek")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "fallback-default" }),
            OnChatAsyncWithCounter = () => fallbackDefaultCalls++,
        };
        var fallbackNamedProvider = new StubProvider("openai")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "fallback-named" }),
            OnChatAsyncWithCounter = () => fallbackNamedCalls++,
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackNamedProvider,
                    ["deepseek"] = fallbackDefaultProvider,
                },
                defaultName: "deepseek"),
            options: new LLMProviderFailoverOptions
            {
                PreferFallbackDefaultProvider = true,
                FallbackToDefaultProviderWhenNamedProviderMissing = true,
            });

        var response = await factory.GetProvider("openai").ChatAsync(new LLMRequest { Messages = [] });

        fallbackDefaultCalls.Should().Be(1);
        fallbackNamedCalls.Should().Be(0);
        response.Content.Should().Be("fallback-default");
    }

    [Fact]
    public async Task ChatAsync_WhenPrimaryReturnsEmpty_ShouldFallback()
    {
        var fallbackCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "" }),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatAsync = static (_, _) => Task.FromResult(new LLMResponse { Content = "fallback-non-empty" }),
            OnChatAsyncWithCounter = () => fallbackCalls++,
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        var response = await factory.GetProvider("openai").ChatAsync(new LLMRequest { Messages = [] });

        fallbackCalls.Should().Be(1);
        response.Content.Should().Be("fallback-non-empty");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryThrowsBeforeMeaningfulChunk_ShouldFallback()
    {
        var fallbackStreamCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => ThrowingStream(),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackStreamCalls++;
                return ContentStream(["fallback-stream"]);
            },
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        var chunks = await ReadAllAsync(factory.GetProvider("openai").ChatStreamAsync(new LLMRequest { Messages = [] }));

        fallbackStreamCalls.Should().Be(1);
        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-stream");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryEmitsMeaningfulChunkThenFails_ShouldNotFallback()
    {
        var fallbackStreamCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => StreamThenThrow(),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackStreamCalls++;
                return ContentStream(["fallback-stream"]);
            },
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        Func<Task> act = async () => await ReadAllAsync(factory.GetProvider("openai").ChatStreamAsync(new LLMRequest { Messages = [] }));

        await act.Should().ThrowAsync<InvalidOperationException>();
        fallbackStreamCalls.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryStreamHasNoMeaningfulOutput_ShouldFallback()
    {
        var fallbackStreamCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => EmptyMeaninglessStream(),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackStreamCalls++;
                return ContentStream(["fallback-after-empty-stream"]);
            },
        };

        var factory = new FailoverLLMProviderFactory(
            primaryFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = primaryProvider,
                },
                defaultName: "openai"),
            fallbackFactory: new StubFactory(
                providers: new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = fallbackProvider,
                },
                defaultName: "openai"));

        var chunks = await ReadAllAsync(factory.GetProvider("openai").ChatStreamAsync(new LLMRequest { Messages = [] }));

        fallbackStreamCalls.Should().Be(1);
        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-after-empty-stream");
    }

    private static async Task<List<LLMStreamChunk>> ReadAllAsync(IAsyncEnumerable<LLMStreamChunk> stream)
    {
        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in stream)
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<LLMStreamChunk> ContentStream(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            yield return new LLMStreamChunk { DeltaContent = part };
            await Task.Yield();
        }

        yield return new LLMStreamChunk { IsLast = true };
    }

    private static async IAsyncEnumerable<LLMStreamChunk> ThrowingStream()
    {
        if (DateTime.UtcNow.Ticks < 0)
            yield return new LLMStreamChunk();

        await Task.Yield();
        throw new InvalidOperationException("stream failed");
    }

    private static async IAsyncEnumerable<LLMStreamChunk> StreamThenThrow()
    {
        yield return new LLMStreamChunk { DeltaContent = "primary-content" };
        await Task.Yield();
        throw new InvalidOperationException("stream failed after meaningful chunk");
    }

    private static async IAsyncEnumerable<LLMStreamChunk> EmptyMeaninglessStream()
    {
        yield return new LLMStreamChunk { IsLast = true };
        await Task.Yield();
    }

    private sealed class StubFactory : ILLMProviderFactory
    {
        private readonly IReadOnlyDictionary<string, ILLMProvider> _providers;
        private readonly string _defaultName;
        private readonly Exception? _throwOnGetProvider;
        private readonly Exception? _throwOnGetDefault;

        public StubFactory(
            IReadOnlyDictionary<string, ILLMProvider>? providers = null,
            string? defaultName = null,
            Exception? throwOnGetProvider = null,
            Exception? throwOnGetDefault = null)
        {
            _providers = providers ?? new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
            _defaultName = defaultName ?? _providers.Keys.FirstOrDefault() ?? "default";
            _throwOnGetProvider = throwOnGetProvider;
            _throwOnGetDefault = throwOnGetDefault;
        }

        public ILLMProvider GetProvider(string name)
        {
            if (_throwOnGetProvider != null)
                throw _throwOnGetProvider;
            return _providers.GetValueOrDefault(name)
                   ?? throw new InvalidOperationException($"provider '{name}' not found");
        }

        public ILLMProvider GetDefault()
        {
            if (_throwOnGetDefault != null)
                throw _throwOnGetDefault;
            return GetProvider(_defaultName);
        }

        public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();
    }

    private sealed class StubProvider(string name) : ILLMProvider
    {
        public string Name => name;

        public Func<LLMRequest, CancellationToken, Task<LLMResponse>>? OnChatAsync { get; init; }
        public Func<LLMRequest, CancellationToken, IAsyncEnumerable<LLMStreamChunk>>? OnChatStreamAsync { get; init; }
        public Action? OnChatAsyncWithCounter { get; init; }

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            OnChatAsyncWithCounter?.Invoke();
            return OnChatAsync != null
                ? OnChatAsync(request, ct)
                : Task.FromResult(new LLMResponse { Content = "ok" });
        }

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            CancellationToken ct = default)
        {
            return OnChatStreamAsync != null
                ? OnChatStreamAsync(request, ct)
                : ContentStream(["ok"]);
        }
    }
}
