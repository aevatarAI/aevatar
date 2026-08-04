using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdResponsesModelsAggregatorTests
{
    [Fact]
    public void NormalizeModelsBody_ShouldPrefixEveryIdWithServiceSlug_RegardlessOfSource()
    {
        // OpenRouter-style: gateway-provider entries (anthropic, openai...) AND
        // proxy-service entries (chrono-llm...) ALL come out as `slug/model`. The
        // earlier mixed-shape design (bare for gateway, prefixed for proxy) was
        // replaced because consistent prefixing makes Stage 2's route resolution
        // uniform: every incoming `vendor/model` always goes through the
        // catalog-backed IResponsesRouteResolver, no source-dependent branching.
        var body = """{"data":[{"id":"claude-opus-4-7"},{"id":"claude-sonnet-4-6"}]}""";
        var anthropicGateway = new NyxIdLlmService(
            CatalogEntryId: "anthropic",
            ServiceSlug: "anthropic",
            DisplayName: "Anthropic",
            RouteValue: "/api/v1/llm/anthropic/v1",
            ModelCatalog: EmptyCatalog(),
            Status: "ready",
            Source: NyxIdLlmProviderSource.GatewayProvider,
            Allowed: true,
            Description: null);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, anthropicGateway);

        entries.Should().HaveCount(2);
        entries[0].Id.Should().Be("anthropic/claude-opus-4-7");
        entries[0].Group.Should().Be("anthropic");
        entries[0].OwnedBy.Should().Be("anthropic");
        entries[0].RouteValue.Should().Be("/api/v1/llm/anthropic/v1");
        entries[1].Id.Should().Be("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public void NormalizeModelsBody_ShouldPrefixProxyServiceEntriesToo()
    {
        var body = """{"data":[{"id":"gpt-4o"},{"id":"qwen-3"}]}""";
        var chronoLlm = new NyxIdLlmService(
            CatalogEntryId: "chrono-llm-id",
            ServiceSlug: "chrono-llm",
            DisplayName: "Chrono LLM",
            RouteValue: "/api/v1/proxy/s/chrono-llm",
            ModelCatalog: EmptyCatalog(),
            Status: "ready",
            Source: NyxIdLlmProviderSource.ProxyService,
            Allowed: true,
            Description: null);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, chronoLlm);

        entries.Select(e => e.Id).Should().Equal("chrono-llm/gpt-4o", "chrono-llm/qwen-3");
    }

    [Fact]
    public void NormalizeModelsBody_ShouldForwardAnthropicShapeMetadata()
    {
        // Anthropic per-provider plane returns `max_input_tokens` (context) and `max_tokens`
        // (output), plus `display_name`. Forward all three so OpenRouter-spec clients get
        // accurate sizing hints; otherwise CC Switch falls back to conservative defaults.
        var body = """
        {"data":[{
            "type":"model",
            "id":"claude-opus-4-7",
            "display_name":"Claude Opus 4.7",
            "created_at":"2026-04-14T00:00:00Z",
            "max_input_tokens":1000000,
            "max_tokens":128000
        }]}
        """;
        var anthropic = MakeService("anthropic", "/api/v1/llm/anthropic/v1", NyxIdLlmProviderSource.GatewayProvider);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, anthropic);

        entries.Should().HaveCount(1);
        var entry = entries[0];
        entry.Id.Should().Be("anthropic/claude-opus-4-7");
        entry.ContextLength.Should().Be(1000000);
        entry.MaxOutputTokens.Should().Be(128000);
        entry.DisplayName.Should().Be("Claude Opus 4.7");
        entry.Created.Should().Be(new DateTimeOffset(2026, 4, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Fact]
    public void NormalizeModelsBody_ShouldForwardOpenRouterShapeMetadata()
    {
        // OpenRouter-spec uses `context_length` + `max_output_tokens` (and `description`).
        // The reader must accept those names too — that's the canonical shape for any
        // backend that follows OpenRouter rather than anthropic conventions.
        var body = """
        {"data":[{
            "id":"x-large",
            "context_length":200000,
            "max_output_tokens":8192,
            "description":"Hypothetical model"
        }]}
        """;
        var service = MakeService("vendor-x", "/api/v1/proxy/s/vendor-x", NyxIdLlmProviderSource.ProxyService);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, service);

        entries.Should().HaveCount(1);
        var entry = entries[0];
        entry.ContextLength.Should().Be(200000);
        entry.MaxOutputTokens.Should().Be(8192);
        entry.Description.Should().Be("Hypothetical model");
    }

    [Fact]
    public void NormalizeModelsBody_ShouldLeaveMetadataNullWhenUpstreamIsSparse()
    {
        // chrono-llm / deepseek / vanilla OpenAI return the minimal `{id, object, created,
        // owned_by}` shape — no metadata to forward. Entry must have nulls so the JSON
        // serializer (configured with JsonIgnoreCondition.WhenWritingNull) omits the
        // fields entirely; aevatar must not invent metadata it doesn't have.
        var body = """{"data":[{"id":"gpt-3.5-turbo","object":"model","created":1700000000,"owned_by":"openai"}]}""";
        var service = MakeService("chrono-llm", "/api/v1/proxy/s/chrono-llm", NyxIdLlmProviderSource.ProxyService);

        var entries = NyxIdResponsesModelsAggregator.NormalizeModelsBody(body, service);

        entries.Should().HaveCount(1);
        var entry = entries[0];
        entry.ContextLength.Should().BeNull();
        entry.MaxOutputTokens.Should().BeNull();
        entry.DisplayName.Should().BeNull();
        entry.Description.Should().BeNull();
    }

    [Fact]
    public void ApplyMetadataFallbacks_ShouldFillOnlyNullFields_PreferringSpecificOverGroup()
    {
        // Lookup precedence: `slug/model` (exact) > `slug` (group). Fallback NEVER
        // overwrites a field that came from upstream — only fills nulls.
        var entries = new List<ResponsesModelEntry>
        {
            new() { Id = "deepseek/deepseek-v4-pro", Created = 0, OwnedBy = "deepseek", Group = "deepseek",
                    RouteValue = "/r", Status = "ready" }, // fully sparse
            new() { Id = "deepseek/deepseek-v4-flash", Created = 0, OwnedBy = "deepseek", Group = "deepseek",
                    RouteValue = "/r", Status = "ready",
                    ContextLength = 32000 }, // partial upstream
            new() { Id = "anthropic/claude-opus-4-7", Created = 0, OwnedBy = "anthropic", Group = "anthropic",
                    RouteValue = "/r", Status = "ready",
                    ContextLength = 1000000, MaxOutputTokens = 128000 }, // fully rich
        };
        var fallbacks = new Dictionary<string, ResponsesModelMetadataFallback>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek"] = new() { ContextLength = 64000, MaxOutputTokens = 8192, DisplayName = "DeepSeek default" },
            ["deepseek/deepseek-v4-pro"] = new() { ContextLength = 128000, MaxOutputTokens = 16384 },
        };

        var merged = NyxIdResponsesModelsAggregator.ApplyMetadataFallbacks(entries, fallbacks);

        // Specific override (deepseek/deepseek-v4-pro) wins for both fields it sets;
        // DisplayName not set in specific → group-level NOT consulted because specific matched first.
        merged[0].ContextLength.Should().Be(128000);
        merged[0].MaxOutputTokens.Should().Be(16384);
        merged[0].DisplayName.Should().BeNull();
        // Partial upstream entry: upstream ContextLength=32000 wins (no overwrite); group fills MaxOutputTokens + DisplayName.
        merged[1].ContextLength.Should().Be(32000);
        merged[1].MaxOutputTokens.Should().Be(8192);
        merged[1].DisplayName.Should().Be("DeepSeek default");
        // Anthropic entry: no fallback configured → untouched.
        merged[2].ContextLength.Should().Be(1000000);
        merged[2].MaxOutputTokens.Should().Be(128000);
    }

    [Fact]
    public void ApplyMetadataFallbacks_ShouldNoOp_WhenFallbackDictIsEmpty()
    {
        var entries = new List<ResponsesModelEntry>
        {
            new() { Id = "deepseek/x", Created = 0, OwnedBy = "deepseek", Group = "deepseek",
                    RouteValue = "/r", Status = "ready" },
        };

        var merged = NyxIdResponsesModelsAggregator.ApplyMetadataFallbacks(
            entries,
            new Dictionary<string, ResponsesModelMetadataFallback>());

        merged.Should().BeSameAs(entries);
    }

    private static NyxIdLlmService MakeService(string slug, string routeValue, string source) =>
        new(
            CatalogEntryId: slug,
            ServiceSlug: slug,
            DisplayName: slug,
            RouteValue: routeValue,
            ModelCatalog: EmptyCatalog(),
            Status: "ready",
            Source: source,
            Allowed: true,
            Description: null);

    private static LLMModelCatalog EmptyCatalog() =>
        LLMSelectionPolicy.NormalizeCatalog(
            [],
            null,
            LLMModelCatalogDiagnosticKind.NotPublished);


    [Theory]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com", "/api/v1/llm/anthropic/v1/", "https://nyx.example.com/api/v1/llm/anthropic/v1/models")]
    [InlineData("https://nyx.example.com/", "/api/v1/proxy/s/chrono-llm", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/models")]
    [InlineData("https://nyx.example.com", "/api/v1/proxy/s/chrono-llm/", "https://nyx.example.com/api/v1/proxy/s/chrono-llm/models")]
    public void BuildModelsUrl_ShouldAppendModelsToRoute(string authority, string routeValue, string expected)
    {
        // GatewayProvider routes are already `/v1`-terminated; ProxyService routes terminate at
        // the slug but NyxID's DownstreamService.base_url itself already embeds `/v1` (e.g.
        // chrono-llm.base_url=https://llm.aelf.dev/v1 → `/proxy/s/chrono-llm/models` lands at
        // https://llm.aelf.dev/v1/models). Both planes correctly accept a single appended
        // `/models`; appending `/v1/models` would double the segment on the proxy plane and 404.
        NyxIdResponsesModelsAggregator.BuildModelsUrl(authority, routeValue).Should().Be(expected);
    }
}
