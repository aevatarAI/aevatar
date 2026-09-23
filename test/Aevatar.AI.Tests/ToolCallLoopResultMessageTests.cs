using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.Tests;

public sealed class ToolCallLoopResultMessageTests
{
    [Fact]
    public void BuildToolResultMessage_ShouldExposeNarrowOrnnSearchResultToModelAndKeepDisplayTextForUi()
    {
        const string skillId = "31b32927-165c-44cf-97c3-ab4ce0a9fb1b";
        const string result = $$"""
            {
              "result_type": "skill_search",
              "status": "success",
              "query": "smoke-test-hello-20260923-a",
              "scope": "mixed",
              "error": null,
              "matches": [
                {
                  "skill_id": "{{skillId}}",
                  "skill_name": "smoke-test-hello-20260923-a",
                  "description": "must not enter model projection",
                  "is_private": true,
                  "category": "plain",
                  "tags": ["private-tag"]
                }
              ],
              "text": "rendered display"
            }
            """;

        var message = ToolCallLoop.BuildToolResultMessage("call-search", "ornn_search_skills", result);

        message.Content.Should().NotBe("rendered display");
        using var document = JsonDocument.Parse(message.Content!);
        var root = document.RootElement;
        root.GetProperty("result_type").GetString().Should().Be("skill_search");
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("query").GetString().Should().Be("smoke-test-hello-20260923-a");
        root.GetProperty("scope").GetString().Should().Be("mixed");
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var match = root.GetProperty("matches").EnumerateArray().Should().ContainSingle().Subject;
        match.GetProperty("skill_id").GetString().Should().Be(skillId);
        match.GetProperty("skill_name").GetString().Should().Be("smoke-test-hello-20260923-a");
        match.TryGetProperty("description", out _).Should().BeFalse();
        match.TryGetProperty("tags", out _).Should().BeFalse();

        var search = message.ToolResultView!.SkillSearch!;
        search.DisplayText.Should().Be("rendered display");
        search.Matches[0].SkillId.Should().Be(skillId);
    }

    [Fact]
    public void BuildToolResultMessage_ShouldKeepConnectedServiceProjectionDataAsText()
    {
        const string result = """
            {
              "kind": "connected_service_read_projection",
              "status": "succeeded",
              "content_boundary": "untrusted_external_data_only",
              "instructions_allowed": false,
              "data": "{\"timezone\":\"Asia/Singapore\",\"total_capacity\":24}"
            }
            """;

        var message = ToolCallLoop.BuildToolResultMessage("call-1", "nyxop_rules", result);

        message.Content.Should().Be(result);
        message.ContentParts.Should().BeNull();
    }

    [Fact]
    public void BuildToolResultMessage_ShouldStillExtractNestedImageData()
    {
        const string result = """
            {
              "image": {
                "data": "aW1hZ2U=",
                "mime_type": "image/png"
              },
              "text": "rendered chart"
            }
            """;

        var message = ToolCallLoop.BuildToolResultMessage("call-1", "render_chart", result);

        message.Content.Should().Be("rendered chart");
        message.ContentParts.Should().NotBeNull();
        message.ContentParts!.Should().ContainSingle(part =>
            part.Kind == ContentPartKind.Image &&
            part.DataBase64 == "aW1hZ2U=" &&
            part.MediaType == "image/png");
    }
}
