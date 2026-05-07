using System.Net;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillClientTests
{
    [Fact]
    public async Task SearchSkillsAsync_SendsNormalizedSearchRequest()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "total": 1,
                "totalPages": 1,
                "page": 1,
                "pageSize": 100,
                "items": [
                  {
                    "guid": "skill-1",
                    "name": "Translate",
                    "description": "Translate text",
                    "isPrivate": true,
                    "tags": ["language"],
                    "metadata": { "category": "text", "tag": ["fallback"] }
                  }
                ]
              }
            }
            """);
        var client = CreateClient(handler, "https://ornn.example/");

        var result = await client.SearchSkillsAsync(
            "access-token",
            "hello world",
            "invalid",
            page: 0,
            pageSize: 500,
            mode: "semantic");

        result.Total.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].Name.Should().Be("Translate");
        result.Items[0].Tags.Should().Equal("language");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Authorization.Should().NotBeNull();
        request.Authorization!.Scheme.Should().Be("Bearer");
        request.Authorization.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://ornn.example/api/web/skill-search?query=hello%20world&mode=semantic&scope=mixed&page=1&pageSize=100");
    }

    [Fact]
    public async Task SearchSkillsAsync_ReturnsEmptyResultWhenBaseUrlMissing()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": null }""");
        var client = CreateClient(handler, "");

        var result = await client.SearchSkillsAsync("access-token", "query");

        result.Items.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSkillsAsync_ReturnsErrorWhenRequestFails()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "error": "nope" }""", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.SearchSkillsAsync("access-token", "query");

        result.Items.Should().BeEmpty();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetSkillJsonAsync_ReturnsSkillFiles()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translate",
                "description": "Translate text",
                "metadata": { "category": "text", "tag": ["language"] },
                "files": { "SKILL.md": "Use this skill." }
              }
            }
            """);
        var client = CreateClient(handler, "https://ornn.example/");

        var skill = await client.GetSkillJsonAsync("access-token", "Translate Skill");

        skill.Should().NotBeNull();
        skill!.Name.Should().Be("Translate");
        skill.Metadata!.Tags.Should().Equal("language");
        skill.Files.Should().ContainKey("SKILL.md");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Authorization!.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be("https://ornn.example/api/web/skills/Translate%20Skill/json");
    }

    [Fact]
    public async Task GetSkillJsonAsync_ReturnsNullWhenRequestFails()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "error": "missing" }""", HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var skill = await client.GetSkillJsonAsync("access-token", "missing");

        skill.Should().BeNull();
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler, string baseUrl = "https://ornn.example")
    {
        return new OrnnSkillClient(
            new OrnnOptions { BaseUrl = baseUrl },
            new HttpClient(handler));
    }
}
