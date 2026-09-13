using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolCallLoopResultMessageTests
{
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
