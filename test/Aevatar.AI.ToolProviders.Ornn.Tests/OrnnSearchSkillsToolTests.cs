using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSearchSkillsToolTests
{
    [Fact]
    public void Contract_ShouldAdvertiseCatalogListingWithoutRequiredQuery()
    {
        var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }"""));

        tool.IsReadOnly.Should().BeTrue();
        tool.SideEffectKind.Should().BeEmpty();
        tool.Description.Should().Contain("asks which Ornn skills they have");
        tool.Description.Should().Contain("empty or omitted query");

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("properties").TryGetProperty("query", out _).Should().BeTrue();
        schema.RootElement.TryGetProperty("required", out _).Should().BeFalse();

        // The model-facing `scope` knob is intentionally gone: a discovery-for-use tool must never
        // let the model narrow visibility and hide skills it can actually use. Always searches mixed.
        schema.RootElement.GetProperty("properties").TryGetProperty("scope", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAuthenticationErrorWhenTokenMissing()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = null;
            var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }"""));

            var result = await tool.ExecuteAsync("""{ "query": "translate" }""");

            ExtractText(result).Should().Contain("No NyxID access token");
            ExtractStatus(result).Should().Be("error");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllowsOmittedQueryForCatalogListing()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "total": 1,
                "items": [
                  {
                    "name": "review-commit-push",
                    "description": "Review local changes and publish them",
                    "isPrivate": true,
                    "metadata": { "category": "git" }
                  }
                ]
              }
            }
            """);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            });
            var tool = CreateTool(handler);

            var result = await tool.ExecuteAsync("{}");
            var text = ExtractText(result);

            text.Should().Contain("Found 1 skills");
            text.Should().Contain("**review-commit-push**");
            ExtractStatus(result).Should().Be("success");

            var request = handler.Requests.Should().ContainSingle().Subject;
            request.RequestUri!.ToString().Should().Contain("query=");
            request.RequestUri!.ToString().Should().Contain("scope=mixed");
            // Discovery requests the server max page so a normal catalog comes back in one call
            // instead of being silently truncated to the old 20-item first page.
            request.RequestUri!.ToString().Should().Contain("pageSize=100");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFormattedSearchResults()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "total": 1,
                "items": [
                  {
                    "name": "Translate",
                    "description": "Translate text",
                    "isPrivate": true,
                    "metadata": { "category": "text", "tag": ["language"] }
                  }
                ]
              }
            }
            """);
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            });
            var tool = CreateTool(handler);

            var result = await tool.ExecuteAsync("""{ "query": "translate", "scope": "private" }""");
            var text = ExtractText(result);

            text.Should().Contain("Found 1 skills");
            text.Should().Contain("**Translate** (private, text)");
            text.Should().Contain("Translate text");
            text.Should().Contain("Tags: language");
            ExtractStatus(result).Should().Be("success");

            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Authorization!.Parameter.Should().Be("access-token");
            request.RequestUri!.ToString().Should().Contain("query=translate");
            // A model-supplied scope is ignored — the request is always scope=mixed.
            request.RequestUri!.ToString().Should().Contain("scope=mixed");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresModelScopePublic_StillSearchesMixed()
    {
        // Regression guard for the original bug: the model picked scope=public for "org-shared
        // skills", and the server returns only isPrivate:false for public — excluding every
        // org-shared skill. With the scope knob removed, public must collapse to mixed.
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }""");
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            });
            var tool = CreateTool(handler);

            await tool.ExecuteAsync("""{ "query": "office", "scope": "public" }""");

            var request = handler.Requests.Should().ContainSingle().Subject;
            request.RequestUri!.ToString().Should().Contain("scope=mixed");
            request.RequestUri!.ToString().Should().NotContain("scope=public");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSearchFailureWhenClientFails()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            });
            var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson(
                """{ "error": "bad" }""",
                System.Net.HttpStatusCode.BadGateway));

            var result = await tool.ExecuteAsync("""{ "query": "translate" }""");

            ExtractText(result).Should().Contain("Search failed:");
            ExtractStatus(result).Should().Be("error");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesDefaultsForMalformedArguments()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }""");
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            });
            var tool = CreateTool(handler);

            var result = await tool.ExecuteAsync("not-json");

            ExtractText(result).Should().Contain("No skills found for query '' (scope: mixed).");
            ExtractStatus(result).Should().Be("no_match");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    private static OrnnSearchSkillsTool CreateTool(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new Aevatar.AI.ToolProviders.NyxId.NyxIdApiClient(
            new Aevatar.AI.ToolProviders.NyxId.NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var client = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn" },
            nyxClient);

        return new OrnnSearchSkillsTool(client);
    }

    private static string ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    private static string ExtractStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("status").GetString() ?? string.Empty;
    }
}
