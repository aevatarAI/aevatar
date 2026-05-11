using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillClientTests
{
    [Fact]
    public async Task SearchSkillsAsync_RoutesThroughNyxIdProxyWithNormalizedQuery()
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
        var client = CreateClient(handler);

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
            "https://nyx.example/api/v1/proxy/s/ornn/api/v1/skill-search?query=hello%20world&mode=semantic&scope=mixed&page=1&pageSize=100");
    }

    [Fact]
    public async Task SearchSkillsAsync_HonorsCustomNyxIdSlug()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }""");
        var client = CreateClient(handler, slug: "ornn-tenant-a");

        await client.SearchSkillsAsync("token", "anything");

        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsoluteUri.Should().StartWith("https://nyx.example/api/v1/proxy/s/ornn-tenant-a/api/v1/skill-search");
    }

    [Fact]
    public async Task SearchSkillsAsync_SurfacesGenericNyxIdProxyErrorWithStatus()
    {
        // NyxIdApiClient wraps non-2xx responses as {"error":true,"status":N,"body":"..."}.
        // The client must surface a concise error rather than a confusing JsonException
        // about the wrapper shape.
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "nope" }""",
            HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.SearchSkillsAsync("token", "query");

        result.Items.Should().BeEmpty();
        result.Error.Should().Contain("status=500");
    }

    [Fact]
    public async Task SearchSkillsAsync_OnNyxIdProxy404_SurfacesSlugBindingHint()
    {
        // 404 from NyxID proxy means the slug isn't resolvable — the user hasn't bound an
        // Ornn service or the deployment's slug differs. The LLM-facing error must tell the
        // model exactly that so it can guide the user rather than retry mechanically (which
        // is what we observed in mainnet after the first NyxID-proxy refactor).
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "missing" }""",
            HttpStatusCode.NotFound);
        var client = CreateClient(handler, slug: "ornn");

        var result = await client.SearchSkillsAsync("token", "query");

        result.Items.Should().BeEmpty();
        result.Error.Should().Contain("slug 'ornn'");
        result.Error.Should().Contain("nyxid_services action=create");
    }

    [Fact]
    public async Task GetSkillJsonAsync_RoutesThroughNyxIdProxyAndReturnsSkillFiles()
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
        var client = CreateClient(handler);

        var skill = await client.GetSkillJsonAsync("access-token", "Translate Skill");

        skill.Should().NotBeNull();
        skill!.Name.Should().Be("Translate");
        skill.Metadata!.Tags.Should().Equal("language");
        skill.Files.Should().ContainKey("SKILL.md");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Authorization!.Parameter.Should().Be("access-token");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/Translate%20Skill/json");
    }

    [Fact]
    public async Task GetSkillJsonAsync_ReturnsNullWhenNyxIdProxyReportsError()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(
            """{ "error": "missing" }""",
            HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var skill = await client.GetSkillJsonAsync("token", "missing");

        skill.Should().BeNull();
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler, string slug = "ornn")
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnSkillClient(new OrnnOptions { NyxIdSlug = slug }, nyxClient);
    }
}
