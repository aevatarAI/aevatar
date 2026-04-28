using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Unit tests for NyxIdProxyTool dual-token routing helpers:
/// ParseServiceSlugs, InMemoryServiceDiscoveryCache.
/// </summary>
public class NyxIdProxyToolDualTokenTests
{
    // ─── ParseServiceSlugs ───

    [Fact]
    public void ParseServiceSlugs_ArrayOfServices_ExtractsSlugs()
    {
        var json = """[{"slug":"api-github","name":"GitHub"},{"slug":"llm-openai","name":"OpenAI"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().HaveCount(2);
        slugs.Should().Contain("api-github");
        slugs.Should().Contain("llm-openai");
    }

    [Fact]
    public void ParseServiceSlugs_EmptyArray_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("[]");

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().BeEmpty();
    }

    [Fact]
    public void ParseServiceSlugs_PaginatedProxyResponse_ExtractsBothServiceGroups()
    {
        using var doc = JsonDocument.Parse("""
            {
              "services":[{"slug":"api-github"}],
              "custom_services":[{"slug":"api-lark-bot"}],
              "total":2,
              "page":1,
              "per_page":100
            }
            """);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().Contain("api-github");
        slugs.Should().Contain("api-lark-bot");
    }

    [Fact]
    public void ParseServiceSlugs_NonArrayRoot_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"error":"unauthorized"}""");

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().BeEmpty();
    }

    [Fact]
    public void ParseServiceSlugs_MissingSlugField_SkipsEntry()
    {
        var json = """[{"slug":"api-github"},{"name":"no-slug-here"},{"slug":"llm-openai"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().HaveCount(2);
        slugs.Should().Contain("api-github");
        slugs.Should().Contain("llm-openai");
    }

    [Fact]
    public void ParseServiceSlugs_CaseInsensitive()
    {
        var json = """[{"slug":"Api-GitHub"}]""";
        using var doc = JsonDocument.Parse(json);

        var slugs = NyxIdProxyTool.ParseServiceSlugs(doc);

        slugs.Should().Contain("api-github");
        slugs.Should().Contain("API-GITHUB");
    }

    // ─── InMemoryServiceDiscoveryCache ───

    [Fact]
    public void Cache_SetAndGet_ReturnsSlugs()
    {
        var cache = new InMemoryServiceDiscoveryCache();
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api-github", "llm-openai" };

        cache.SetSlugs("token-hash-1", slugs);
        var result = cache.GetSlugs("token-hash-1");

        result.Should().NotBeNull();
        result.Should().Contain("api-github");
        result.Should().Contain("llm-openai");
    }

    [Fact]
    public void Cache_Miss_ReturnsNull()
    {
        var cache = new InMemoryServiceDiscoveryCache();

        var result = cache.GetSlugs("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void Cache_OverwritesSameKey()
    {
        var cache = new InMemoryServiceDiscoveryCache();
        cache.SetSlugs("hash-1", new HashSet<string> { "old-service" });
        cache.SetSlugs("hash-1", new HashSet<string> { "new-service" });

        var result = cache.GetSlugs("hash-1");

        result.Should().Contain("new-service");
        result.Should().NotContain("old-service");
    }

    [Fact]
    public void Cache_DifferentKeys_Independent()
    {
        var cache = new InMemoryServiceDiscoveryCache();
        cache.SetSlugs("user-hash", new HashSet<string> { "user-service" });
        cache.SetSlugs("org-hash", new HashSet<string> { "org-service" });

        cache.GetSlugs("user-hash").Should().Contain("user-service");
        cache.GetSlugs("user-hash").Should().NotContain("org-service");
        cache.GetSlugs("org-hash").Should().Contain("org-service");
        cache.GetSlugs("org-hash").Should().NotContain("user-service");
    }

    // ─── LooksLikeErrorEnvelope ───

    [Theory]
    [InlineData("""{"error":true,"status":401,"body":""}""")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void LooksLikeErrorEnvelope_TruthyError_True(string input)
    {
        // Used by DiscoverMergedServicesAsync to short-circuit when both user and org
        // discovery returned NyxID error envelopes — without it, the merge synthesizes
        // an empty array and the SkillRunner safety net misclassifies the run as
        // successful (PR #471 review round 2).
        NyxIdProxyTool.LooksLikeErrorEnvelope(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"error":false,"data":{}}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""[{"slug":"api-github"}]""")]
    [InlineData("plain text")]
    [InlineData("")]
    public void LooksLikeErrorEnvelope_NotAnEnvelope_False(string input)
    {
        NyxIdProxyTool.LooksLikeErrorEnvelope(input).Should().BeFalse();
    }
}
