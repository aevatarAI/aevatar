using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnRemoteSkillDiscoveryTests
{
    [Fact]
    public async Task SearchSkillsAsync_ReturnsEmptyWhenRequestCannotSearch()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }""");
        var discovery = CreateDiscovery(handler);

        var missingToken = await discovery.SearchSkillsAsync(new RemoteSkillSearchRequest("", "translate"));
        var missingQuery = await discovery.SearchSkillsAsync(new RemoteSkillSearchRequest("token", ""));

        missingToken.Should().BeEmpty();
        missingQuery.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSkillsAsync_MapsNamedSkillsAndDropsUnnamedItems()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "items": [
                  {
                    "guid": " skill-1 ",
                    "name": " Translate ",
                    "description": " Translate text ",
                    "isPrivate": true,
                    "tags": ["language"],
                    "metadata": { "category": "text", "tag": ["fallback"] }
                  },
                  {
                    "guid": "skill-2",
                    "name": "   ",
                    "description": "ignored",
                    "isPrivate": false
                  },
                  {
                    "guid": "",
                    "name": "Summarize",
                    "description": null,
                    "isPrivate": false,
                    "metadata": { "category": "writing", "tag": ["summary"] }
                  }
                ]
              }
            }
            """);
        var discovery = CreateDiscovery(handler);

        var result = await discovery.SearchSkillsAsync(new RemoteSkillSearchRequest(
            "access-token",
            "translate",
            Scope: "private",
            Mode: "semantic",
            PageSize: 5));

        result.Should().HaveCount(2);
        result[0].Should().BeEquivalentTo(new RemoteSkillSummary(
            "Translate",
            "Translate text",
            RemoteId: "skill-1",
            IsPrivate: true,
            Category: "text",
            Tags: ["language"]));
        result[1].Name.Should().Be("Summarize");
        result[1].Description.Should().BeEmpty();
        result[1].RemoteId.Should().BeNull();
        result[1].Tags.Should().Equal("summary");

        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.ToString().Should().Contain("mode=semantic");
    }

    [Fact]
    public async Task SearchSkillsAsync_ReturnsEmptyWhenClientReportsError()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""{ "error": "bad" }""", System.Net.HttpStatusCode.BadGateway);
        var discovery = CreateDiscovery(handler);

        var result = await discovery.SearchSkillsAsync(new RemoteSkillSearchRequest("access-token", "translate"));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSkillsAsync_RejectsNullRequest()
    {
        var discovery = CreateDiscovery(OrnnTestHttpMessageHandler.ReturningJson("""{ "data": { "items": [] } }"""));

        var act = () => discovery.SearchSkillsAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static OrnnRemoteSkillDiscovery CreateDiscovery(OrnnTestHttpMessageHandler handler)
    {
        var client = new OrnnSkillClient(
            new OrnnOptions { BaseUrl = "https://ornn.example" },
            new HttpClient(handler));

        return new OrnnRemoteSkillDiscovery(client);
    }
}
