using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.LLMProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class FailoverLLMProviderFactoryTests
{
    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryMissing_ShouldResolveFromFallback()
    {
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => ContentStream(["fallback-ok"]),
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
        var chunks = await ReadAllAsync(provider.ChatStreamAsync(new LLMRequest { Messages = [] }));

        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-ok");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryThrowsBeforeMeaningfulChunk_ShouldFallbackToSecondary()
    {
        var fallbackCalls = 0;

        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => ThrowingStream(),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackCalls++;
                return ContentStream(["fallback-response"]);
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

        fallbackCalls.Should().Be(1);
        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-response");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPreferFallbackDefaultEnabled_ShouldUseFallbackDefaultProvider()
    {
        var fallbackDefaultCalls = 0;
        var fallbackNamedCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => ThrowingStream(),
        };
        var fallbackDefaultProvider = new StubProvider("deepseek")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackDefaultCalls++;
                return ContentStream(["fallback-default"]);
            },
        };
        var fallbackNamedProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackNamedCalls++;
                return ContentStream(["fallback-named"]);
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
                    ["openai"] = fallbackNamedProvider,
                    ["deepseek"] = fallbackDefaultProvider,
                },
                defaultName: "deepseek"),
            options: new LLMProviderFailoverOptions
            {
                PreferFallbackDefaultProvider = true,
                FallbackToDefaultProviderWhenNamedProviderMissing = true,
            });

        var chunks = await ReadAllAsync(factory.GetProvider("openai").ChatStreamAsync(new LLMRequest { Messages = [] }));

        fallbackDefaultCalls.Should().Be(1);
        fallbackNamedCalls.Should().Be(0);
        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-default");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenPrimaryCompletesWithoutMeaningfulOutput_ShouldFallback()
    {
        var fallbackCalls = 0;
        var primaryProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = static (_, _) => EmptyMeaninglessStream(),
        };
        var fallbackProvider = new StubProvider("openai")
        {
            OnChatStreamAsync = (_, _) =>
            {
                fallbackCalls++;
                return ContentStream(["fallback-non-empty"]);
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

        fallbackCalls.Should().Be(1);
        chunks.Select(x => x.DeltaContent).Should().Contain("fallback-non-empty");
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

        public Func<LLMRequest, CancellationToken, IAsyncEnumerable<LLMStreamChunk>>? OnChatStreamAsync { get; init; }

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
