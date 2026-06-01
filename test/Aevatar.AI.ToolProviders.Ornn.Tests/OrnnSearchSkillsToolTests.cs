using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSearchSkillsToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsAuthenticationErrorWhenTokenMissing()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = null;
            var tool = CreateTool(OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }"""));

            var result = await tool.ExecuteAsync("""{ "query": "translate" }""");

            result.Should().Contain("No NyxID access token");
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

            result.Should().Contain("Found 1 skills");
            result.Should().Contain("**Translate** (private, text)");
            result.Should().Contain("Translate text");
            result.Should().Contain("Tags: language");

            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Authorization!.Parameter.Should().Be("access-token");
            request.RequestUri!.ToString().Should().Contain("query=translate");
            request.RequestUri!.ToString().Should().Contain("scope=private");
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

            result.Should().Contain("Search failed:");
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

            result.Should().Contain("No skills found for query '' (scope: mixed).");
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
}
